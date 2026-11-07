using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ControlCenter.Models;
using Microsoft.Data.Sqlite;

namespace ControlCenter.Services;

/// <summary>
/// Unified local SQLite database service for DAAMS Control Center (Offline-First Architecture).
/// 
/// Interacts directly with the single shared SQLite database accessible by both
/// the Python Monitoring Engine and the C# WPF Control Center.
/// Uses WAL mode, parameterized queries, and safe concurrent access with busy_timeout.
/// </summary>
public class LocalDatabaseService
{
    private static readonly Regex RiskFactorTextPattern = new(@"^(?<description>.*?)\s*\(\+?(?<weight>-?\d+)\)\s*$", RegexOptions.Compiled);
    private readonly string _databasePath;
    private readonly string _connectionString;

    public string DatabasePath => _databasePath;
    public bool DatabaseFound { get; }

    public LocalDatabaseService()
    {
        _databasePath = ResolveDatabasePath();
        DatabaseFound = File.Exists(_databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,   // never auto-create; the Python engine owns the file
            DefaultTimeout = 5
        }.ToString();
        // EnsureSchema() call intentionally removed: Python (database.py) owns the schema.
    }

    /// <summary>
    /// Resolves the canonical shared database path across applications on the same machine.
    /// Priority:
    ///   1. DAAMS_DATABASE_PATH environment variable
    ///   2. Repo-relative path (monitoring_engine\data\daams.db)
    ///   3. ProgramData\DAAMS\daams.db fallback
    /// </summary>
    public static string ResolveDatabasePath()
    {
        string? envPath = Environment.GetEnvironmentVariable("DAAMS_DATABASE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return Path.GetFullPath(envPath.Trim());

        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "monitoring_engine", "data", "daams.db");
            if (File.Exists(candidate))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DAAMS", "data", "daams.db");
    }

    // =========================================================================
    // Cloud fallback (Control Center on a PC that has no local database)
    //
    // Offline-first rule:
    //   - Local database present  -> read local SQLite (works with no internet;
    //                                the Monitoring Engine syncs it up to Firestore).
    //   - Local database missing  -> read the same data from Firestore instead.
    // Cloud mode is READ-ONLY: write methods below still need the local database.
    // MainViewModel sets CloudFallback once at startup.
    // =========================================================================

    public static FirestoreService? CloudFallback { get; set; }

    public bool CloudFallbackActive => !DatabaseFound && CloudFallback is not null;

    private static FirestoreService RequireCloud() =>
        CloudFallback ?? throw new FileNotFoundException("No local DAAMS database and no cloud connection configured.");

    public async Task<List<ProtectedAsset>> GetProtectedAssetsAsync() =>
        DatabaseFound ? await GetProtectedAssetsLocalAsync() : await RequireCloud().GetProtectedAssetsAsync();

    public async Task<List<ActivityLog>> GetActivityLogsAsync() =>
        DatabaseFound ? await GetActivityLogsLocalAsync() : await RequireCloud().GetActivityLogsAsync();

    public async Task<List<SecurityAlert>> GetSecurityAlertsAsync() =>
        DatabaseFound ? await GetSecurityAlertsLocalAsync() : await RequireCloud().GetSecurityAlertsAsync();

    public async Task<List<IncidentReport>> GetIncidentReportsAsync() =>
        DatabaseFound ? await GetIncidentReportsLocalAsync() : await RequireCloud().GetIncidentReportsAsync();

    public async Task<List<CaseItem>> GetCasesAsync()
    {
        if (DatabaseFound)
        {
            return await GetCasesLocalAsync();
        }

        FirestoreService cloud = RequireCloud();
        Task<List<SecurityAlert>> alertsTask = cloud.GetSecurityAlertsAsync();
        Task<List<IncidentReport>> incidentsTask = cloud.GetIncidentReportsAsync();
        await Task.WhenAll(alertsTask, incidentsTask);
        return BuildCasesFromCloud(alertsTask.Result, incidentsTask.Result);
    }

    /// <summary>Same merge the SQL join in GetCasesLocalAsync does: one case per alert, using the linked incident's status when there is one.</summary>
    private static List<CaseItem> BuildCasesFromCloud(List<SecurityAlert> alerts, List<IncidentReport> incidents)
    {
        var latestIncidentByAlert = incidents
            .Where(i => !string.IsNullOrEmpty(i.RelatedAlertId))
            .GroupBy(i => i.RelatedAlertId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.UpdatedAt).First());

        var list = new List<CaseItem>();
        foreach (SecurityAlert alert in alerts.OrderByDescending(a => a.CreatedAt))
        {
            latestIncidentByAlert.TryGetValue(alert.AlertId, out IncidentReport? incident);

            CaseStatus status = incident is not null
                ? incident.InvestigationStatus switch
                {
                    InvestigationStatus.UnderInvestigation => CaseStatus.Investigating,
                    InvestigationStatus.Resolved => CaseStatus.Resolved,
                    InvestigationStatus.Dismissed => CaseStatus.Dismissed,
                    _ => CaseStatus.New
                }
                : alert.Status switch
                {
                    AlertStatus.Investigating => CaseStatus.Investigating,
                    AlertStatus.Reviewed => CaseStatus.Dismissed,
                    AlertStatus.Resolved => CaseStatus.Resolved,
                    _ => CaseStatus.New
                };

            list.Add(new CaseItem
            {
                AlertId = alert.AlertId,
                IncidentId = incident?.IncidentId,
                User = alert.User,
                Device = alert.Device,
                Asset = alert.Asset,
                Path = alert.Path,
                Action = alert.Action,
                Classification = alert.Classification,
                RiskScore = alert.RiskScore,
                RiskLevel = alert.RiskLevel,
                Reason = alert.Reason,
                RiskFactors = alert.RiskFactors,
                DetectedAt = alert.CreatedAt,
                Status = status,
                Notes = incident?.Notes,
                Resolution = incident?.Resolution,
                DateResolved = incident?.DateResolved,
                UpdatedAt = incident?.UpdatedAt ?? alert.CreatedAt,
            });
        }

        return list;
    }

    private SqliteConnection CreateConnection()
    {
        if (!DatabaseFound)
            throw new FileNotFoundException($"DAAMS database not found: {_databasePath}");

        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL,
                    description TEXT NOT NULL
                );

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
                    asset_path TEXT,
                    protected_root TEXT,
                    sync_status TEXT NOT NULL DEFAULT 'PENDING',
                    synced_at TEXT,
                    sync_error TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_activity_logs_timestamp ON activity_logs(timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_activity_logs_sync ON activity_logs(sync_status);

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

                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDatabaseService] Schema initialization warning: {ex}");
        }
    }

    // =========================================================================
    // Protected Assets
    // =========================================================================

    private async Task<List<ProtectedAsset>> GetProtectedAssetsLocalAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<ProtectedAsset>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM protected_assets ORDER BY protected_at DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ProtectedAsset
                {
                    Path = reader.GetString(reader.GetOrdinal("path")),
                    AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
                    AssetType = ParseEnum(reader.GetString(reader.GetOrdinal("asset_type")), AssetType.File),
                    Classification = ParseEnum(reader.GetString(reader.GetOrdinal("classification")), Classification.Public),
                    ProtectedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("protected_at"))),
                    ProtectedBy = reader.GetString(reader.GetOrdinal("protected_by")),
                    Status = ParseEnum(reader.GetString(reader.GetOrdinal("status")), ProtectionStatus.Protected),
                });
            }
            return list;
        });
    }

    public async Task<bool> UpdateAssetClassificationAsync(string path, string classification)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE protected_assets
                    SET classification = @classification,
                        sync_status = 'PENDING',
                        updated_at = @now
                    WHERE path = @path
                       OR path = @path COLLATE NOCASE
                       OR REPLACE(TRIM(path, '/\'), '/', '\') = REPLACE(TRIM(@path, '/\'), '/', '\') COLLATE NOCASE;
                ";
                cmd.Parameters.AddWithValue("@classification", classification);
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@path", path);

                int rows = cmd.ExecuteNonQuery();
                return rows > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to update asset classification: {ex}");
                return false;
            }
        });
    }

    public async Task<bool> RemoveAssetProtectionAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE protected_assets
                    SET status = 'unprotected',
                        sync_status = 'PENDING',
                        updated_at = @now
                    WHERE path = @path OR path = @path COLLATE NOCASE;
                ";
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@path", path);

                int rows = cmd.ExecuteNonQuery();
                return rows > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to remove asset protection: {ex}");
                return false;
            }
        });
    }

    // =========================================================================
    // Security Settings (PIN)
    // =========================================================================

    public async Task<bool> VerifyPinAsync(string pin)
    {
        return await Task.Run(() => SettingsService.VerifyPin(pin, _databasePath));
    }

    // =========================================================================
    // Activity Logs
    // =========================================================================

    private async Task<List<ActivityLog>> GetActivityLogsLocalAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<ActivityLog>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM activity_logs ORDER BY timestamp DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string assetFieldValue = reader.GetString(reader.GetOrdinal("asset"));
                string? assetPath = reader.IsDBNull(reader.GetOrdinal("asset_path"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("asset_path"));
                string? protectedRoot = reader.IsDBNull(reader.GetOrdinal("protected_root"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("protected_root"));
                string? classVal = reader.IsDBNull(reader.GetOrdinal("classification")) ? null : reader.GetString(reader.GetOrdinal("classification"));
                int? riskScore = reader.IsDBNull(reader.GetOrdinal("risk_score")) ? null : reader.GetInt32(reader.GetOrdinal("risk_score"));
                string? riskLevelVal = reader.IsDBNull(reader.GetOrdinal("risk_level")) ? null : reader.GetString(reader.GetOrdinal("risk_level"));
                string? factorsJson = reader.IsDBNull(reader.GetOrdinal("risk_factors")) ? null : reader.GetString(reader.GetOrdinal("risk_factors"));
                string? advisory = reader.IsDBNull(reader.GetOrdinal("advisory")) ? null : reader.GetString(reader.GetOrdinal("advisory"));

                list.Add(new ActivityLog
                {
                    User = reader.GetString(reader.GetOrdinal("user")),
                    Device = reader.GetString(reader.GetOrdinal("device")),
                    Asset = DeriveDisplayName(assetFieldValue),
                    Path = assetFieldValue,
                    AssetPath = assetPath,
                    ProtectedRoot = protectedRoot,
                    Action = ParseEnum(reader.GetString(reader.GetOrdinal("action")), ActionType.Open),
                    Timestamp = ParseTimestamp(reader.GetString(reader.GetOrdinal("timestamp"))),
                    Classification = ParseEnum(classVal ?? "Public", Classification.Public),
                    RiskScore = riskScore,
                    RiskLevel = ParseNullableEnum<RiskLevel>(riskLevelVal),
                    RiskFactors = ParseRiskFactors(factorsJson),
                    Advisory = advisory,
                });
            }
            return list;
        });
    }

    // =========================================================================
    // Security Alerts
    // =========================================================================

    private async Task<List<SecurityAlert>> GetSecurityAlertsLocalAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<SecurityAlert>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM security_alerts ORDER BY timestamp DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? classVal = reader.IsDBNull(reader.GetOrdinal("asset_classification")) ? null : reader.GetString(reader.GetOrdinal("asset_classification"));
                string? factorsJson = reader.IsDBNull(reader.GetOrdinal("risk_factors")) ? null : reader.GetString(reader.GetOrdinal("risk_factors"));
                string? reasonVal = reader.IsDBNull(reader.GetOrdinal("reason")) ? null : reader.GetString(reader.GetOrdinal("reason"));
                var riskFactors = ParseRiskFactors(factorsJson);

                string reason = !string.IsNullOrWhiteSpace(reasonVal)
                    ? reasonVal
                    : SynthesizeReason(riskFactors);

                list.Add(new SecurityAlert
                {
                    AlertId = reader.GetString(reader.GetOrdinal("alert_id")),
                    User = reader.GetString(reader.GetOrdinal("user")),
                    Device = reader.GetString(reader.GetOrdinal("device")),
                    Asset = reader.GetString(reader.GetOrdinal("asset")),
                    Path = reader.GetString(reader.GetOrdinal("asset_path")),
                    Action = ParseEnum(reader.GetString(reader.GetOrdinal("action")), ActionType.Open),
                    Classification = ParseEnum(classVal ?? "Public", Classification.Public),
                    RiskScore = reader.GetInt32(reader.GetOrdinal("risk_score")),
                    RiskLevel = ParseEnum(reader.GetString(reader.GetOrdinal("risk_level")), RiskLevel.Low),
                    Reason = reason,
                    RiskFactors = riskFactors,
                    Status = ParseEnum(reader.GetString(reader.GetOrdinal("status")), AlertStatus.New),
                    CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("timestamp"))),
                });
            }
            return list;
        });
    }

    public async Task<bool> UpdateAlertStatusAsync(string alertId, AlertStatus newStatus)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE security_alerts
                    SET status = @status, sync_status = 'PENDING', updated_at = @now
                    WHERE alert_id = @alertId;
                ";
                cmd.Parameters.AddWithValue("@status", newStatus.ToString());
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@alertId", alertId);

                int rows = cmd.ExecuteNonQuery();
                return rows > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to update alert status: {ex}");
                return false;
            }
        });
    }

    // =========================================================================
    // Incident Reports
    // =========================================================================

    private async Task<List<IncidentReport>> GetIncidentReportsLocalAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<IncidentReport>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM incident_reports ORDER BY updated_at DESC;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string? classVal = reader.IsDBNull(reader.GetOrdinal("classification")) ? null : reader.GetString(reader.GetOrdinal("classification"));
                string? notes = reader.IsDBNull(reader.GetOrdinal("investigation_notes")) ? null : reader.GetString(reader.GetOrdinal("investigation_notes"));
                string? res = reader.IsDBNull(reader.GetOrdinal("resolution")) ? null : reader.GetString(reader.GetOrdinal("resolution"));
                string? dateRes = reader.IsDBNull(reader.GetOrdinal("date_resolved")) ? null : reader.GetString(reader.GetOrdinal("date_resolved"));
                string? relatedAlert = reader.IsDBNull(reader.GetOrdinal("related_alert_id")) ? null : reader.GetString(reader.GetOrdinal("related_alert_id"));

                list.Add(new IncidentReport
                {
                    IncidentId = reader.GetString(reader.GetOrdinal("incident_id")),
                    RelatedAlertId = relatedAlert,
                    User = reader.GetString(reader.GetOrdinal("user")),
                    Device = reader.GetString(reader.GetOrdinal("device")),
                    Asset = reader.GetString(reader.GetOrdinal("asset")),
                    Path = reader.IsDBNull(reader.GetOrdinal("asset_path")) ? null : reader.GetString(reader.GetOrdinal("asset_path")),
                    Action = ParseEnum(reader.GetString(reader.GetOrdinal("action")), ActionType.Open),
                    Classification = ParseEnum(classVal ?? "Public", Classification.Public),
                    Timestamp = ParseTimestamp(reader.GetString(reader.GetOrdinal("timestamp"))),
                    RiskScore = reader.IsDBNull(reader.GetOrdinal("risk_score")) ? 0 : reader.GetInt32(reader.GetOrdinal("risk_score")),
                    RiskLevel = ParseEnum(reader.GetString(reader.GetOrdinal("risk_level")), RiskLevel.Low),
                    Description = reader.GetString(reader.GetOrdinal("description")),
                    InvestigationStatus = ParseEnum(reader.GetString(reader.GetOrdinal("investigation_status")), InvestigationStatus.New),
                    Notes = notes,
                    Resolution = res,
                    DateResolved = string.IsNullOrWhiteSpace(dateRes) ? null : ParseTimestamp(dateRes),
                    CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
                    UpdatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
                });
            }
            return list;
        });
    }

    public async Task<bool> SaveIncidentReportAsync(IncidentReport incident)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO incident_reports
                    (incident_id, related_alert_id, user, device, asset, asset_path, action, classification, timestamp, risk_score, risk_level, risk_factors, description, investigation_status, investigation_notes, resolution, date_resolved, created_at, updated_at, sync_status)
                    VALUES (@id, @alertId, @user, @device, @asset, @assetPath, @action, @class, @ts, @riskScore, @riskLevel, @factors, @desc, @invStatus, @notes, @res, @dateRes, @created, @updated, 'PENDING');
                ";
                cmd.Parameters.AddWithValue("@id", incident.IncidentId);
                cmd.Parameters.AddWithValue("@alertId", (object?)incident.RelatedAlertId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@user", incident.User);
                cmd.Parameters.AddWithValue("@device", incident.Device);
                cmd.Parameters.AddWithValue("@asset", incident.Asset);
                cmd.Parameters.AddWithValue("@assetPath", (object?)incident.Path ?? (object?)incident.Asset ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@action", incident.Action.ToString());
                cmd.Parameters.AddWithValue("@class", incident.Classification.ToString());
                cmd.Parameters.AddWithValue("@ts", incident.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@riskScore", incident.RiskScore);
                cmd.Parameters.AddWithValue("@riskLevel", incident.RiskLevel.ToString());
                cmd.Parameters.AddWithValue("@factors", "[]");
                cmd.Parameters.AddWithValue("@desc", incident.Description);
                cmd.Parameters.AddWithValue("@invStatus", incident.InvestigationStatus.ToString());
                cmd.Parameters.AddWithValue("@notes", (object?)incident.Notes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@res", (object?)incident.Resolution ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@dateRes", incident.DateResolved.HasValue ? incident.DateResolved.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value);
                cmd.Parameters.AddWithValue("@created", incident.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@updated", incident.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to save incident report: {ex}");
                return false;
            }
        });
    }

    public async Task<bool> UpdateIncidentReportStatusAsync(string incidentId, InvestigationStatus status, DateTime? dateResolved)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE incident_reports
                    SET investigation_status = @status,
                        date_resolved = @dateResolved,
                        updated_at = @now,
                        sync_status = 'PENDING'
                    WHERE incident_id = @id;
                ";
                cmd.Parameters.AddWithValue("@status", status.ToString());
                cmd.Parameters.AddWithValue("@dateResolved", dateResolved.HasValue ? dateResolved.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : DBNull.Value);
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", incidentId);

                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to update incident status: {ex}");
                return false;
            }
        });
    }

    public async Task<bool> UpdateIncidentResolutionAsync(string incidentId, string? resolution)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE incident_reports
                    SET resolution = @resolution,
                        updated_at = @now,
                        sync_status = 'PENDING'
                    WHERE incident_id = @id;
                ";
                cmd.Parameters.AddWithValue("@resolution", (object?)resolution ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", incidentId);

                return cmd.ExecuteNonQuery() > 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] Failed to update incident resolution: {ex}");
                return false;
            }
        });
    }

    // =========================================================================
    // Cases (Alert + Incident Merged)
    // =========================================================================

    private async Task<List<CaseItem>> GetCasesLocalAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<CaseItem>();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    a.alert_id,
                    a.user AS a_user,
                    a.device AS a_device,
                    a.asset AS a_asset,
                    a.asset_path AS a_path,
                    a.action AS a_action,
                    a.asset_classification AS a_class,
                    a.risk_score AS a_risk_score,
                    a.risk_level AS a_risk_level,
                    a.risk_factors AS a_risk_factors,
                    a.reason AS a_reason,
                    a.timestamp AS a_timestamp,
                    a.status AS a_status,
                    a.updated_at AS a_updated_at,
                    i.incident_id,
                    i.investigation_status AS i_inv_status,
                    i.investigation_notes AS i_notes,
                    i.resolution AS i_resolution,
                    i.date_resolved AS i_date_resolved,
                    i.updated_at AS i_updated_at
                FROM security_alerts a
                LEFT JOIN incident_reports i ON i.related_alert_id = a.alert_id
                ORDER BY a.timestamp DESC;
            ";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string alertId = reader.GetString(reader.GetOrdinal("alert_id"));
                string user = reader.GetString(reader.GetOrdinal("a_user"));
                string device = reader.GetString(reader.GetOrdinal("a_device"));
                string asset = reader.GetString(reader.GetOrdinal("a_asset"));
                string? path = reader.IsDBNull(reader.GetOrdinal("a_path")) ? null : reader.GetString(reader.GetOrdinal("a_path"));
                string actionStr = reader.GetString(reader.GetOrdinal("a_action"));
                string? classVal = reader.IsDBNull(reader.GetOrdinal("a_class")) ? null : reader.GetString(reader.GetOrdinal("a_class"));
                int riskScore = reader.GetInt32(reader.GetOrdinal("a_risk_score"));
                string riskLevelStr = reader.GetString(reader.GetOrdinal("a_risk_level"));
                string? factorsJson = reader.IsDBNull(reader.GetOrdinal("a_risk_factors")) ? null : reader.GetString(reader.GetOrdinal("a_risk_factors"));
                string? reasonVal = reader.IsDBNull(reader.GetOrdinal("a_reason")) ? null : reader.GetString(reader.GetOrdinal("a_reason"));
                string aTimestamp = reader.GetString(reader.GetOrdinal("a_timestamp"));
                string? aStatus = reader.IsDBNull(reader.GetOrdinal("a_status")) ? null : reader.GetString(reader.GetOrdinal("a_status"));
                string? aUpdatedAt = reader.IsDBNull(reader.GetOrdinal("a_updated_at")) ? null : reader.GetString(reader.GetOrdinal("a_updated_at"));

                string? incidentId = reader.IsDBNull(reader.GetOrdinal("incident_id")) ? null : reader.GetString(reader.GetOrdinal("incident_id"));
                string? iInvStatus = reader.IsDBNull(reader.GetOrdinal("i_inv_status")) ? null : reader.GetString(reader.GetOrdinal("i_inv_status"));
                string? notes = reader.IsDBNull(reader.GetOrdinal("i_notes")) ? null : reader.GetString(reader.GetOrdinal("i_notes"));
                string? resolution = reader.IsDBNull(reader.GetOrdinal("i_resolution")) ? null : reader.GetString(reader.GetOrdinal("i_resolution"));
                string? dateRes = reader.IsDBNull(reader.GetOrdinal("i_date_resolved")) ? null : reader.GetString(reader.GetOrdinal("i_date_resolved"));
                string? iUpdatedAt = reader.IsDBNull(reader.GetOrdinal("i_updated_at")) ? null : reader.GetString(reader.GetOrdinal("i_updated_at"));

                var riskFactors = ParseRiskFactors(factorsJson);
                string reason = !string.IsNullOrWhiteSpace(reasonVal) ? reasonVal : SynthesizeReason(riskFactors);

                CaseStatus caseStatus;
                if (!string.IsNullOrEmpty(incidentId) && !string.IsNullOrEmpty(iInvStatus))
                {
                    caseStatus = iInvStatus.ToLowerInvariant() switch
                    {
                        "new" => CaseStatus.New,
                        "underinvestigation" => CaseStatus.Investigating,
                        "investigating" => CaseStatus.Investigating,
                        "resolved" => CaseStatus.Resolved,
                        "dismissed" => CaseStatus.Dismissed,
                        _ => CaseStatus.New
                    };
                }
                else
                {
                    caseStatus = (aStatus ?? "").ToLowerInvariant() switch
                    {
                        "new" => CaseStatus.New,
                        "investigating" => CaseStatus.Investigating,
                        "reviewed" => CaseStatus.Dismissed,
                        "resolved" => CaseStatus.Resolved,
                        _ => CaseStatus.New
                    };
                }

                string updateStr = !string.IsNullOrWhiteSpace(iUpdatedAt) ? iUpdatedAt : (!string.IsNullOrWhiteSpace(aUpdatedAt) ? aUpdatedAt : aTimestamp);

                list.Add(new CaseItem
                {
                    AlertId = alertId,
                    IncidentId = incidentId,
                    User = user,
                    Device = device,
                    Asset = asset,
                    Path = path,
                    Action = ParseEnum(actionStr, ActionType.Open),
                    Classification = ParseEnum(classVal ?? "Public", Classification.Public),
                    RiskScore = riskScore,
                    RiskLevel = ParseEnum(riskLevelStr, RiskLevel.Low),
                    Reason = reason,
                    RiskFactors = riskFactors,
                    DetectedAt = ParseTimestamp(aTimestamp),
                    Status = caseStatus,
                    Notes = notes,
                    Resolution = resolution,
                    DateResolved = string.IsNullOrWhiteSpace(dateRes) ? null : ParseTimestamp(dateRes),
                    UpdatedAt = ParseTimestamp(updateStr),
                });
            }

            return list;
        });
    }

    public async Task ApplyCaseChangeAsync(string alertId, CaseStatus status, string? notes, string? resolution)
    {
        await Task.Run(() =>
        {
            using var conn = CreateConnection();
            using var trans = conn.BeginTransaction();
            try
            {
                string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                string alertStatusStr = status switch
                {
                    CaseStatus.New => "New",
                    CaseStatus.Investigating => "Investigating",
                    CaseStatus.Dismissed => "Reviewed",
                    CaseStatus.Resolved => "Resolved",
                    _ => "New"
                };

                string incidentStatusStr = status switch
                {
                    CaseStatus.New => "New",
                    CaseStatus.Investigating => "UnderInvestigation",
                    CaseStatus.Dismissed => "Dismissed",
                    CaseStatus.Resolved => "Resolved",
                    _ => "New"
                };

                object dateResolvedObj = (status is CaseStatus.Resolved or CaseStatus.Dismissed)
                    ? (object)nowStr
                    : DBNull.Value;

                // 1. Update security_alerts
                using (var cmdAlert = conn.CreateCommand())
                {
                    cmdAlert.Transaction = trans;
                    cmdAlert.CommandText = @"
                        UPDATE security_alerts
                        SET status = @status, sync_status = 'PENDING', updated_at = @now
                        WHERE alert_id = @alertId;
                    ";
                    cmdAlert.Parameters.AddWithValue("@status", alertStatusStr);
                    cmdAlert.Parameters.AddWithValue("@now", nowStr);
                    cmdAlert.Parameters.AddWithValue("@alertId", alertId);
                    cmdAlert.ExecuteNonQuery();
                }

                // 2. Check if incident exists
                string? existingIncidentId = null;
                using (var cmdCheck = conn.CreateCommand())
                {
                    cmdCheck.Transaction = trans;
                    cmdCheck.CommandText = "SELECT incident_id FROM incident_reports WHERE related_alert_id = @alertId LIMIT 1;";
                    cmdCheck.Parameters.AddWithValue("@alertId", alertId);
                    var scalar = cmdCheck.ExecuteScalar();
                    if (scalar != null && scalar != DBNull.Value)
                    {
                        existingIncidentId = scalar.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(existingIncidentId))
                {
                    // Update existing incident
                    using var cmdUpdateInc = conn.CreateCommand();
                    cmdUpdateInc.Transaction = trans;
                    cmdUpdateInc.CommandText = @"
                        UPDATE incident_reports
                        SET investigation_status = @invStatus,
                            investigation_notes = @notes,
                            resolution = @resolution,
                            date_resolved = @dateResolved,
                            updated_at = @now,
                            sync_status = 'PENDING'
                        WHERE related_alert_id = @alertId;
                    ";
                    cmdUpdateInc.Parameters.AddWithValue("@invStatus", incidentStatusStr);
                    cmdUpdateInc.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
                    cmdUpdateInc.Parameters.AddWithValue("@resolution", (object?)resolution ?? DBNull.Value);
                    cmdUpdateInc.Parameters.AddWithValue("@dateResolved", dateResolvedObj);
                    cmdUpdateInc.Parameters.AddWithValue("@now", nowStr);
                    cmdUpdateInc.Parameters.AddWithValue("@alertId", alertId);
                    cmdUpdateInc.ExecuteNonQuery();
                }
                else
                {
                    // Create incident from alert fields
                    string user = "UNKNOWN", device = "UNKNOWN", asset = "Unknown", actionStr = "Open", classStr = "Public", riskLevelStr = "Low", desc = "Security alert investigation.";
                    string? assetPath = null, timestampStr = nowStr;
                    object riskScoreObj = DBNull.Value;
                    string factorsJson = "[]";

                    using (var cmdFetchAlert = conn.CreateCommand())
                    {
                        cmdFetchAlert.Transaction = trans;
                        cmdFetchAlert.CommandText = @"
                            SELECT user, device, asset, asset_path, action, asset_classification, risk_score, risk_level, risk_factors, reason, timestamp
                            FROM security_alerts
                            WHERE alert_id = @alertId;
                        ";
                        cmdFetchAlert.Parameters.AddWithValue("@alertId", alertId);
                        using var reader = cmdFetchAlert.ExecuteReader();
                        if (reader.Read())
                        {
                            user = reader.GetString(reader.GetOrdinal("user"));
                            device = reader.GetString(reader.GetOrdinal("device"));
                            asset = reader.GetString(reader.GetOrdinal("asset"));
                            assetPath = reader.IsDBNull(reader.GetOrdinal("asset_path")) ? null : reader.GetString(reader.GetOrdinal("asset_path"));
                            actionStr = reader.GetString(reader.GetOrdinal("action"));
                            classStr = reader.IsDBNull(reader.GetOrdinal("asset_classification")) ? "Public" : reader.GetString(reader.GetOrdinal("asset_classification"));
                            if (!reader.IsDBNull(reader.GetOrdinal("risk_score")))
                            {
                                riskScoreObj = reader.GetInt32(reader.GetOrdinal("risk_score"));
                            }
                            riskLevelStr = reader.GetString(reader.GetOrdinal("risk_level"));
                            if (!reader.IsDBNull(reader.GetOrdinal("risk_factors")))
                            {
                                factorsJson = reader.GetString(reader.GetOrdinal("risk_factors"));
                            }
                            if (!reader.IsDBNull(reader.GetOrdinal("reason")))
                            {
                                desc = reader.GetString(reader.GetOrdinal("reason"));
                            }
                            timestampStr = reader.GetString(reader.GetOrdinal("timestamp"));
                        }
                    }

                    string newIncId = alertId.StartsWith("ALERT-", StringComparison.OrdinalIgnoreCase)
                        ? "INC-" + alertId.Substring(6)
                        : $"INC-{DateTime.Now:yyMMddHHmmss}";

                    using (var cmdInsertInc = conn.CreateCommand())
                    {
                        cmdInsertInc.Transaction = trans;
                        cmdInsertInc.CommandText = @"
                            INSERT INTO incident_reports
                            (incident_id, related_alert_id, user, device, asset, asset_path, action, classification, timestamp, risk_score, risk_level, risk_factors, description, investigation_status, investigation_notes, resolution, date_resolved, created_at, updated_at, sync_status)
                            VALUES (@incId, @alertId, @user, @device, @asset, @assetPath, @action, @class, @ts, @riskScore, @riskLevel, @factors, @desc, @invStatus, @notes, @resolution, @dateResolved, @now, @now, 'PENDING');
                        ";
                        cmdInsertInc.Parameters.AddWithValue("@incId", newIncId);
                        cmdInsertInc.Parameters.AddWithValue("@alertId", alertId);
                        cmdInsertInc.Parameters.AddWithValue("@user", user);
                        cmdInsertInc.Parameters.AddWithValue("@device", device);
                        cmdInsertInc.Parameters.AddWithValue("@asset", asset);
                        cmdInsertInc.Parameters.AddWithValue("@assetPath", (object?)assetPath ?? DBNull.Value);
                        cmdInsertInc.Parameters.AddWithValue("@action", actionStr);
                        cmdInsertInc.Parameters.AddWithValue("@class", classStr);
                        cmdInsertInc.Parameters.AddWithValue("@ts", timestampStr);
                        cmdInsertInc.Parameters.AddWithValue("@riskScore", riskScoreObj);
                        cmdInsertInc.Parameters.AddWithValue("@riskLevel", riskLevelStr);
                        cmdInsertInc.Parameters.AddWithValue("@factors", factorsJson);
                        cmdInsertInc.Parameters.AddWithValue("@desc", desc);
                        cmdInsertInc.Parameters.AddWithValue("@invStatus", incidentStatusStr);
                        cmdInsertInc.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
                        cmdInsertInc.Parameters.AddWithValue("@resolution", (object?)resolution ?? DBNull.Value);
                        cmdInsertInc.Parameters.AddWithValue("@dateResolved", dateResolvedObj);
                        cmdInsertInc.Parameters.AddWithValue("@now", nowStr);
                        cmdInsertInc.ExecuteNonQuery();
                    }
                }

                trans.Commit();
            }
            catch (Exception ex)
            {
                trans.Rollback();
                Debug.WriteLine($"[LocalDatabaseService] Failed ApplyCaseChangeAsync: {ex}");
                throw;
            }
        });
    }

    // =========================================================================
    // Sync Status & Metrics
    // =========================================================================

    public async Task<(int totalRecords, int pendingCount)> GetSyncSummaryAsync()
    {
        return await Task.Run(() =>
        {
            int total = 0;
            int pending = 0;
            try
            {
                using var conn = CreateConnection();
                string[] tables = { "protected_assets", "activity_logs", "security_alerts", "incident_reports", "security_settings" };
                foreach (string t in tables)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) as total, SUM(CASE WHEN sync_status IN ('PENDING', 'FAILED') THEN 1 ELSE 0 END) as pending FROM {t};";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        total += reader.GetInt32(0);
                        if (!reader.IsDBNull(1))
                        {
                            pending += reader.GetInt32(1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] GetSyncSummary error: {ex}");
            }
            return (total, pending);
        });
    }

    public async Task<string?> GetLastSyncAttemptAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT attempted_at, status, error_message FROM sync_history ORDER BY id DESC LIMIT 1;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    string attemptedAt = reader.GetString(0);
                    string status = reader.GetString(1);
                    string? error = reader.IsDBNull(2) ? null : reader.GetString(2);
                    return string.IsNullOrWhiteSpace(error)
                        ? $"{attemptedAt} ({status})"
                        : $"{attemptedAt} ({status}: {error})";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalDatabaseService] GetLastSyncAttemptAsync error: {ex}");
            }
            return null;
        });
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
    }

    private static TEnum? ParseNullableEnum<TEnum>(string? value) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : null;
    }

    private static DateTime ParseTimestamp(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }
        return DateTime.MinValue;
    }

    private static string DeriveDisplayName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains(" -> "))
        {
            return value;
        }

        int lastSeparator = value.LastIndexOfAny(new[] { '\\', '/' });
        return lastSeparator >= 0 && lastSeparator < value.Length - 1
            ? value[(lastSeparator + 1)..]
            : value;
    }

    private static List<RiskFactor> ParseRiskFactors(string? json)
    {
        var factors = new List<RiskFactor>();
        if (string.IsNullOrWhiteSpace(json)) return factors;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var parsed = ParseRiskFactorText(el.GetString() ?? string.Empty);
                        if (parsed != null) factors.Add(parsed);
                    }
                }
            }
        }
        catch
        {
            // Fallback for unparseable JSON
        }
        return factors;
    }

    private static RiskFactor? ParseRiskFactorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("has no configured risk weight", StringComparison.OrdinalIgnoreCase))
            return new RiskFactor { Description = "Create: no risk points", Weight = 0 };
        Match match = RiskFactorTextPattern.Match(text);
        if (match.Success && int.TryParse(match.Groups["weight"].Value, out int weight))
        {
            if (weight == 0) return null;
            return new RiskFactor { Description = match.Groups["description"].Value, Weight = weight };
        }
        if (text.EndsWith("(+0)") || text.EndsWith("(0)")) return null;
        return new RiskFactor { Description = text, Weight = 0 };
    }

    private static string SynthesizeReason(List<RiskFactor> riskFactors)
    {
        return riskFactors.Count > 0
            ? string.Join(", ", riskFactors.Select(f => f.Description)) + "."
            : "Flagged by the Risk Assessment Engine.";
    }
}
