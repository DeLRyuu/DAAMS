using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ControlCenter.Models;
using Microsoft.Data.Sqlite;

namespace ControlCenter.Services;

/// <summary>
/// Cloud-first behaviour for LocalDatabaseService.
///
/// READ rule:  online + live cloud data ready  -> Firestore (live cache)
///             otherwise, local database exists -> local SQLite (offline view)
///             otherwise                        -> Firestore (cache or one-shot read)
///
/// WRITE rule: online (or no local database) -> write straight to Firestore
///             offline with a local database -> write SQLite as PENDING; when the internet returns the
///                                              row is pushed to Firestore and then removed locally
/// </summary>
public partial class LocalDatabaseService
{
    private static readonly SemaphoreSlim PushGate = new(1, 1);

    /// <summary>True when the PC has a network AND the Firestore listeners have delivered their first data.</summary>
    private static bool CloudPreferred => CloudFallback is { LiveReady: true } && NetworkInterface.GetIsNetworkAvailable();

    /// <summary>Read from local SQLite only when it exists and the cloud is not the better source right now.</summary>
    private bool UseLocal => DatabaseFound && !CloudPreferred;

    /// <summary>
    /// Alert and incident changes go straight to Firestore when it is reachable (the local copy is removed
    /// after sync, so the row may not exist locally) or when there is no local database at all.
    /// </summary>
    private bool WriteToCloud => !DatabaseFound || CloudPreferred;

    /// <summary>For the status footer, so it is obvious which source the screens are showing.</summary>
    public string DataSourceText => CloudPreferred ? "Showing: Cloud (live)" : (DatabaseFound ? "Showing: Local (offline)" : "Showing: Cloud");

    // =========================================================================
    // Push pending alert / incident rows to Firestore
    // =========================================================================

    private sealed record PendingRow(string Id, string Version, Dictionary<string, object?> Fields);

    /// <summary>
    /// Uploads alert and incident rows still marked PENDING/FAILED. Never throws. Does nothing without a local
    /// database, without a cloud link, or without a network. A row is marked SYNCED only if it was not changed
    /// again while uploading (compared by updated_at), so a newer edit is never lost.
    /// </summary>
    public async Task TryPushPendingCasesAsync()
    {
        FirestoreService? cloud = CloudFallback;
        if (cloud is null || !DatabaseFound || !NetworkInterface.GetIsNetworkAvailable()) return;
        if (!await PushGate.WaitAsync(0)) return;

        try
        {
            List<PendingRow> alerts = await Task.Run(ReadPendingAlertRows);
            foreach (PendingRow row in alerts)
            {
                await cloud.SetDocumentAsync("security_alerts", row.Id, row.Fields);
                await Task.Run(() => RemoveRowAfterPush("security_alerts", "alert_id", row.Id, row.Version));
            }

            List<PendingRow> incidents = await Task.Run(ReadPendingIncidentRows);
            foreach (PendingRow row in incidents)
            {
                await cloud.SetDocumentAsync("incident_reports", row.Id, row.Fields);
                await Task.Run(() => RemoveRowAfterPush("incident_reports", "incident_id", row.Id, row.Version));
            }
        }
        catch (Exception ex)
        {
            // Offline or Firestore error: rows stay PENDING and are retried on the next refresh tick.
            Debug.WriteLine($"[LocalDatabaseService] Push to cloud failed: {ex.Message}");
        }
        finally
        {
            PushGate.Release();
        }
    }

    private static string? Str(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static List<string> FactorList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private List<PendingRow> ReadPendingAlertRows()
    {
        var rows = new List<PendingRow>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT alert_id, activity_id, user, device, asset, asset_path, action, asset_classification,
                   risk_score, risk_level, risk_factors, reason, timestamp, status, investigation_status, updated_at
            FROM security_alerts
            WHERE sync_status IN ('PENDING', 'FAILED')
            LIMIT 50;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string id = r.GetString(r.GetOrdinal("alert_id"));
            string updated = Str(r, "updated_at") ?? string.Empty;
            rows.Add(new PendingRow(id, updated, new Dictionary<string, object?>
            {
                ["alert_id"] = id,
                ["activity_id"] = Str(r, "activity_id"),
                ["user"] = Str(r, "user"),
                ["device"] = Str(r, "device"),
                ["asset"] = Str(r, "asset"),
                ["asset_path"] = Str(r, "asset_path"),
                ["action"] = Str(r, "action"),
                ["asset_classification"] = Str(r, "asset_classification"),
                ["risk_score"] = r.IsDBNull(r.GetOrdinal("risk_score")) ? (object?)null : (long)r.GetInt32(r.GetOrdinal("risk_score")),
                ["risk_level"] = Str(r, "risk_level"),
                ["risk_factors"] = FactorList(Str(r, "risk_factors")),
                ["reason"] = Str(r, "reason"),
                ["timestamp"] = Str(r, "timestamp"),
                ["status"] = Str(r, "status"),
                ["investigation_status"] = Str(r, "investigation_status"),
                ["updated_at"] = updated,
            }));
        }
        return rows;
    }

    private List<PendingRow> ReadPendingIncidentRows()
    {
        var rows = new List<PendingRow>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT incident_id, related_alert_id, user, device, asset, asset_path, action, classification, timestamp,
                   risk_score, risk_level, risk_factors, description, investigation_status, investigation_notes,
                   resolution, date_resolved, created_at, updated_at
            FROM incident_reports
            WHERE sync_status IN ('PENDING', 'FAILED')
            LIMIT 50;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string id = r.GetString(r.GetOrdinal("incident_id"));
            string updated = Str(r, "updated_at") ?? string.Empty;
            rows.Add(new PendingRow(id, updated, new Dictionary<string, object?>
            {
                ["incident_id"] = id,
                ["related_alert_id"] = Str(r, "related_alert_id"),
                ["user"] = Str(r, "user"),
                ["device"] = Str(r, "device"),
                ["asset"] = Str(r, "asset"),
                ["asset_path"] = Str(r, "asset_path"),
                ["action"] = Str(r, "action"),
                ["classification"] = Str(r, "classification"),
                ["timestamp"] = Str(r, "timestamp"),
                ["risk_score"] = r.IsDBNull(r.GetOrdinal("risk_score")) ? (object?)null : (long)r.GetInt32(r.GetOrdinal("risk_score")),
                ["risk_level"] = Str(r, "risk_level"),
                ["risk_factors"] = FactorList(Str(r, "risk_factors")),
                ["description"] = Str(r, "description"),
                ["investigation_status"] = Str(r, "investigation_status"),
                ["investigation_notes"] = Str(r, "investigation_notes"),
                ["resolution"] = Str(r, "resolution"),
                ["date_resolved"] = Str(r, "date_resolved"),
                ["created_at"] = Str(r, "created_at"),
                ["updated_at"] = updated,
            }));
        }
        return rows;
    }

    /// <summary>
    /// The row is now confirmed in Firestore, so remove it from the local database. It is removed only if it was not
    /// changed again while uploading (compared by updated_at); otherwise it stays PENDING and is pushed again.
    /// </summary>
    private void RemoveRowAfterPush(string table, string idColumn, string id, string version)
    {
        // table and idColumn are fixed literals from this file, never user input.
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE {idColumn} = @id AND updated_at = @version;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.ExecuteNonQuery();
    }

    // =========================================================================
    // Cloud-only writes (this PC has no local database)
    // =========================================================================

    private async Task<bool> CloudUpdateAlertStatusAsync(string alertId, AlertStatus status)
    {
        try
        {
            await RequireCloud().SetDocumentAsync("security_alerts", alertId, new Dictionary<string, object?>
            {
                ["alert_id"] = alertId,
                ["status"] = status.ToString(),
                ["updated_at"] = FirestoreService.FormatTime(DateTime.Now),
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDatabaseService] Cloud alert update failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CloudSaveIncidentAsync(IncidentReport incident)
    {
        try
        {
            var fields = new Dictionary<string, object?>
            {
                ["incident_id"] = incident.IncidentId,
                ["related_alert_id"] = incident.RelatedAlertId,
                ["user"] = incident.User,
                ["device"] = incident.Device,
                ["asset"] = incident.Asset,
                ["asset_path"] = incident.Path ?? incident.Asset,
                ["action"] = incident.Action.ToString(),
                ["classification"] = incident.Classification.ToString(),
                ["timestamp"] = FirestoreService.FormatTime(incident.Timestamp),
                ["risk_score"] = (long)incident.RiskScore,
                ["risk_level"] = incident.RiskLevel.ToString(),
                ["description"] = incident.Description,
                ["investigation_status"] = incident.InvestigationStatus.ToString(),
                // The Engine names this field investigation_notes (NOT notes).
                ["investigation_notes"] = incident.Notes,
                ["resolution"] = incident.Resolution,
                ["date_resolved"] = incident.DateResolved.HasValue ? FirestoreService.FormatTime(incident.DateResolved.Value) : null,
                ["created_at"] = FirestoreService.FormatTime(incident.CreatedAt),
                ["updated_at"] = FirestoreService.FormatTime(incident.UpdatedAt),
            };
            await RequireCloud().SetDocumentAsync("incident_reports", incident.IncidentId, fields);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDatabaseService] Cloud incident save failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CloudUpdateIncidentStatusAsync(string incidentId, InvestigationStatus status, DateTime? dateResolved)
    {
        try
        {
            await RequireCloud().SetDocumentAsync("incident_reports", incidentId, new Dictionary<string, object?>
            {
                ["incident_id"] = incidentId,
                ["investigation_status"] = status.ToString(),
                ["date_resolved"] = dateResolved.HasValue ? FirestoreService.FormatTime(dateResolved.Value) : null,
                ["updated_at"] = FirestoreService.FormatTime(DateTime.Now),
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDatabaseService] Cloud incident status update failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CloudUpdateIncidentResolutionAsync(string incidentId, string? resolution)
    {
        try
        {
            await RequireCloud().SetDocumentAsync("incident_reports", incidentId, new Dictionary<string, object?>
            {
                ["incident_id"] = incidentId,
                ["resolution"] = resolution,
                ["updated_at"] = FirestoreService.FormatTime(DateTime.Now),
            });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalDatabaseService] Cloud incident resolution update failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Cloud version of ApplyCaseChangeAsync: same status mapping and incident creation, written to Firestore. Throws on failure so the screen shows the error.</summary>
    private async Task CloudApplyCaseChangeAsync(string alertId, CaseStatus status, string? notes, string? resolution)
    {
        FirestoreService cloud = RequireCloud();
        DateTime now = DateTime.Now;

        SecurityAlert alert = (await cloud.GetSecurityAlertsAsync()).FirstOrDefault(a => a.AlertId == alertId)
            ?? throw new InvalidOperationException($"Alert {alertId} was not found in the cloud.");
        IncidentReport? incident = (await cloud.GetIncidentReportsAsync())
            .Where(i => i.RelatedAlertId == alertId)
            .OrderByDescending(i => i.UpdatedAt)
            .FirstOrDefault();

        string alertStatus = status switch
        {
            CaseStatus.Investigating => "Investigating",
            CaseStatus.Dismissed => "Reviewed",
            CaseStatus.Resolved => "Resolved",
            _ => "New"
        };
        string incidentStatus = status switch
        {
            CaseStatus.Investigating => "UnderInvestigation",
            CaseStatus.Dismissed => "Dismissed",
            CaseStatus.Resolved => "Resolved",
            _ => "New"
        };
        bool closing = status is CaseStatus.Resolved or CaseStatus.Dismissed;
        string nowText = FirestoreService.FormatTime(now);

        await cloud.SetDocumentAsync("security_alerts", alertId, new Dictionary<string, object?>
        {
            ["alert_id"] = alertId,
            ["status"] = alertStatus,
            ["updated_at"] = nowText,
        });

        string incidentId = incident?.IncidentId
            ?? (alertId.StartsWith("ALERT-", StringComparison.OrdinalIgnoreCase)
                ? "INC-" + alertId.Substring(6)
                : $"INC-{now:yyMMddHHmmss}");

        var fields = new Dictionary<string, object?>
        {
            ["incident_id"] = incidentId,
            ["related_alert_id"] = alertId,
            ["investigation_status"] = incidentStatus,
            ["investigation_notes"] = notes,
            ["resolution"] = resolution,
            ["date_resolved"] = closing ? nowText : null,
            ["updated_at"] = nowText,
        };

        if (incident is null)
        {
            // First time this alert becomes an incident: copy the alert details, same as the local version does.
            fields["user"] = alert.User;
            fields["device"] = alert.Device;
            fields["asset"] = alert.Asset;
            fields["asset_path"] = alert.Path;
            fields["action"] = alert.Action.ToString();
            fields["classification"] = alert.Classification.ToString();
            fields["timestamp"] = FirestoreService.FormatTime(alert.CreatedAt);
            fields["risk_score"] = (long)alert.RiskScore;
            fields["risk_level"] = alert.RiskLevel.ToString();
            fields["risk_factors"] = alert.RiskFactors
                .Select(f => f.Weight != 0 ? $"{f.Description} (+{f.Weight})" : f.Description)
                .ToList();
            fields["description"] = string.IsNullOrWhiteSpace(alert.Reason) ? "Security alert investigation." : alert.Reason;
            fields["created_at"] = nowText;
        }

        await cloud.SetDocumentAsync("incident_reports", incidentId, fields);
    }
}
