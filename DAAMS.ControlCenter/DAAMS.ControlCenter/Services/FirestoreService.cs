using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ControlCenter.Models;
using Google.Cloud.Firestore;

namespace ControlCenter.Services;

/// <summary>
/// Read-only data access for the four DAAMS Firestore collections
/// (activity_logs, protected_assets, security_alerts, incident_reports).
///
/// All Firestore-document-to-model mapping happens here, in one place, so
/// field-name assumptions are easy to find and fix if the real Monitoring
/// System's output differs from what's assumed below — see the note above
/// the Map* methods.
///
/// This is Phase 3, and Phase 3 is intentionally read-only: there are no
/// write/delete/update methods here on purpose (reference doc integration
/// rule — the Control Center displays Monitoring System data, it doesn't
/// alter it).
/// </summary>
public class FirestoreService
{
    private const string ActivityLogsCollection = "activity_logs";
    private const string ProtectedAssetsCollection = "protected_assets";
    private const string SecurityAlertsCollection = "security_alerts";
    private const string IncidentReportsCollection = "incident_reports";

    private readonly FirestoreConnectionService _connection;

    public FirestoreService(FirestoreConnectionService connection)
    {
        _connection = connection;
    }

    public async Task<List<ProtectedAsset>> GetProtectedAssetsAsync()
    {
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(ProtectedAssetsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapProtectedAsset).ToList();
    }

    public async Task<List<ActivityLog>> GetActivityLogsAsync()
    {
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(ActivityLogsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapActivityLog).ToList();
    }

    public async Task<List<SecurityAlert>> GetSecurityAlertsAsync()
    {
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(SecurityAlertsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapSecurityAlert).ToList();
    }

    public async Task<List<IncidentReport>> GetIncidentReportsAsync()
    {
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(IncidentReportsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapIncidentReport).ToList();
    }

    // ------------------------------------------------------------------
    // MAPPING — field-name confidence, collection by collection:
    //
    //   protected_assets   CONFIRMED — matches the reference doc's own JSON
    //                       example (§5) exactly: path, asset_name, asset_type,
    //                       classification, protected_at, protected_by, status.
    //
    //   activity_logs      PARTIALLY CONFIRMED — user/device/asset/action/
    //                       timestamp match the reference doc's basic record
    //                       example (§10) exactly. classification/risk_score/
    //                       risk_level/risk_factors are listed as "extended"
    //                       fields but not shown as a concrete example, so
    //                       their exact names are inferred using the
    //                       snake_case convention protected_assets already
    //                       established.
    //
    //   security_alerts,   NOT YET CONFIRMED — the reference doc (§21,
    //   incident_reports   "Current Development Boundary") states risk
    //                       assessment, alerts, and incidents were not yet
    //                       implemented in the Monitoring System as of the
    //                       last check. Field names below are a reasonable
    //                       best guess, not a verified contract.
    //
    // Every Get*/Map* helper below tolerates a missing or mismatched field
    // (falls back to a safe default) rather than throwing, per the reference
    // doc's instruction not to let optional/missing fields crash the app.
    // If the real Monitoring System uses different field names, this is the
    // only file that needs to change — no Model or ViewModel edits required.
    // ------------------------------------------------------------------

    private static ProtectedAsset MapProtectedAsset(DocumentSnapshot doc) => new()
    {
        Path = GetString(doc, "path", doc.Id),
        AssetName = GetString(doc, "asset_name", doc.Id),
        AssetType = GetEnum(doc, "asset_type", AssetType.File),
        Classification = GetEnum(doc, "classification", Classification.Public),
        ProtectedAt = GetTimestamp(doc, "protected_at"),
        ProtectedBy = GetString(doc, "protected_by", "unknown"),
        Status = GetEnum(doc, "status", ProtectionStatus.Unprotected),
    };

    private static ActivityLog MapActivityLog(DocumentSnapshot doc) => new()
    {
        User = GetString(doc, "user", "unknown"),
        Device = GetString(doc, "device", "unknown"),
        Asset = GetString(doc, "asset", "unknown"),
        Path = doc.TryGetValue("path", out string? path) ? path : null,
        Action = GetEnum(doc, "action", ActionType.Open),
        Timestamp = GetTimestamp(doc, "timestamp"),
        Classification = GetEnum(doc, "classification", Classification.Public),
        RiskScore = GetNullableInt(doc, "risk_score"),
        RiskLevel = GetNullableEnum<RiskLevel>(doc, "risk_level"),
        RiskFactors = GetRiskFactors(doc),
    };

    private static SecurityAlert MapSecurityAlert(DocumentSnapshot doc) => new()
    {
        // No confirmed "alert ID" field — falls back to the Firestore document
        // ID, matching the auto-ID pattern the reference doc already describes
        // for activity_logs (§11: "Firestore auto-generates the document ID").
        AlertId = GetString(doc, "alert_id", doc.Id),
        User = GetString(doc, "user", "unknown"),
        Device = GetString(doc, "device", "unknown"),
        Asset = GetString(doc, "asset", "unknown"),
        Action = GetEnum(doc, "action", ActionType.Open),
        RiskScore = GetNullableInt(doc, "risk_score") ?? 0,
        RiskLevel = GetEnum(doc, "risk_level", RiskLevel.Low),
        Reason = GetString(doc, "reason", "No reason provided."),
        Status = GetEnum(doc, "status", AlertStatus.New),
        CreatedAt = GetTimestamp(doc, "created_at"),
    };

    private static IncidentReport MapIncidentReport(DocumentSnapshot doc) => new()
    {
        IncidentId = GetString(doc, "incident_id", doc.Id),
        RelatedAlertId = doc.TryGetValue("related_alert_id", out string? relatedAlert) ? relatedAlert : null,
        User = GetString(doc, "user", "unknown"),
        Device = GetString(doc, "device", "unknown"),
        Asset = GetString(doc, "asset", "unknown"),
        Action = GetEnum(doc, "action", ActionType.Open),
        Timestamp = GetTimestamp(doc, "timestamp"),
        RiskLevel = GetEnum(doc, "risk_level", RiskLevel.Low),
        Description = GetString(doc, "description", string.Empty),
        InvestigationStatus = GetEnum(doc, "investigation_status", InvestigationStatus.New),
        Notes = doc.TryGetValue("notes", out string? notes) ? notes : null,
        CreatedAt = GetTimestamp(doc, "created_at"),
        UpdatedAt = GetTimestamp(doc, "updated_at"),
    };

    // --- field-read helpers: every one tolerates a missing/mismatched field ---

    private static string GetString(DocumentSnapshot doc, string field, string fallback)
        => doc.TryGetValue(field, out string? value) && !string.IsNullOrEmpty(value) ? value : fallback;

    private static TEnum GetEnum<TEnum>(DocumentSnapshot doc, string field, TEnum fallback) where TEnum : struct, Enum
        => doc.TryGetValue(field, out string? raw) && Enum.TryParse(raw, ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;

    private static TEnum? GetNullableEnum<TEnum>(DocumentSnapshot doc, string field) where TEnum : struct, Enum
        => doc.TryGetValue(field, out string? raw) && Enum.TryParse(raw, ignoreCase: true, out TEnum parsed)
            ? parsed
            : null;

    private static int? GetNullableInt(DocumentSnapshot doc, string field)
    {
        if (doc.TryGetValue(field, out long longValue))
        {
            return (int)longValue;
        }

        if (doc.TryGetValue(field, out double doubleValue))
        {
            return (int)doubleValue;
        }

        return null;
    }

    /// <summary>
    /// Reads a timestamp field that may be either a native Firestore Timestamp
    /// (if the Monitoring System writes datetime objects) or a plain string in
    /// "yyyy-MM-dd HH:mm:ss" form (matching the format shown throughout the
    /// reference doc's JSON examples — Python's json.dumps of a formatted
    /// string, not a Firestore Timestamp). Tries both; falls back to
    /// DateTime.MinValue — treated by the UI as "timestamp unavailable" — if
    /// neither works, rather than throwing.
    /// </summary>
    private static DateTime GetTimestamp(DocumentSnapshot doc, string field)
    {
        if (doc.TryGetValue(field, out Timestamp ts))
        {
            return ts.ToDateTime().ToLocalTime();
        }

        if (doc.TryGetValue(field, out string? raw) &&
            DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// Tolerates either shape: an array of maps ({"description": ..., "weight": ...})
    /// or a plain array of strings. Unknown/unparseable entries are skipped
    /// rather than failing the whole document.
    /// </summary>
    private static List<RiskFactor> GetRiskFactors(DocumentSnapshot doc)
    {
        var factors = new List<RiskFactor>();

        if (!doc.TryGetValue("risk_factors", out object? raw) || raw is not IEnumerable<object> items)
        {
            return factors;
        }

        foreach (object item in items)
        {
            switch (item)
            {
                case IDictionary<string, object> map:
                    string description = map.TryGetValue("description", out object? d) ? d?.ToString() ?? string.Empty : string.Empty;
                    int weight = map.TryGetValue("weight", out object? w) && w is not null ? Convert.ToInt32(w) : 0;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        factors.Add(new RiskFactor { Description = description, Weight = weight });
                    }
                    break;

                case string s when !string.IsNullOrWhiteSpace(s):
                    factors.Add(new RiskFactor { Description = s, Weight = 0 });
                    break;
            }
        }

        return factors;
    }
}
