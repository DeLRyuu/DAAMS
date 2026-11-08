using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
/// Phase 7 cross-checked every field name below against the actual
/// Monitoring Engine source (its own README.md, which documents real,
/// tested JSON examples — not the earlier Control Center reference doc,
/// which turned out to disagree with the real implementation in a few
/// places). See the per-collection notes above each Map* method for exactly
/// what changed and why.
///
/// Reads are served from a live in-memory cache fed by Firestore snapshot listeners
/// (see FirestoreService.Live.cs). The Monitoring Engine still writes activity_logs,
/// protected_assets and new security_alerts; the Control Center now also writes alert
/// status changes and incident reports back (merge writes, so Engine fields are kept).
/// </summary>
public partial class FirestoreService
{
    private const string ActivityLogsCollection = "activity_logs";
    private const string ProtectedAssetsCollection = "protected_assets";
    private const string SecurityAlertsCollection = "security_alerts";
    private const string IncidentReportsCollection = "incident_reports";
    private const string SecuritySettingsCollection = "security_settings";

    private readonly FirestoreConnectionService _connection;

    public FirestoreService(FirestoreConnectionService connection)
    {
        _connection = connection;
    }

    public async Task<List<ProtectedAsset>> GetProtectedAssetsAsync()
    {
        if (_liveAssets.Ready) return _liveAssets.Snapshot();
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(ProtectedAssetsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapProtectedAsset).ToList();
    }

    public async Task<List<ActivityLog>> GetActivityLogsAsync()
    {
        if (_liveActivity.Ready) return _liveActivity.Snapshot();
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(ActivityLogsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapActivityLog).ToList();
    }

    public async Task<List<SecurityAlert>> GetSecurityAlertsAsync()
    {
        if (_liveAlerts.Ready) return _liveAlerts.Snapshot();
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(SecurityAlertsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapSecurityAlert).ToList();
    }

    /// <summary>
    /// Queries the incident_reports collection. Per the Phase 7 schema
    /// inspection (see the MAPPING notes below), this collection most likely
    /// doesn't exist yet in the real Firestore project — the Monitoring
    /// Engine has no incident-management write path. Firestore returns an
    /// empty snapshot for a collection with no documents rather than
    /// erroring, so this stays safe either way; it just won't return
    /// anything until the Monitoring Engine (or a later Control Center
    /// phase) actually writes incidents here.
    /// </summary>
    public async Task<List<IncidentReport>> GetIncidentReportsAsync()
    {
        if (_liveIncidents.Ready) return _liveIncidents.Snapshot();
        FirestoreDb db = await _connection.GetDbAsync();
        QuerySnapshot snapshot = await db.Collection(IncidentReportsCollection).GetSnapshotAsync();
        return snapshot.Documents.Select(MapIncidentReport).ToList();
    }

    public async Task<string> GetCloudDataSummaryAsync(bool includeAll)
    {
        FirestoreDb db = await _connection.GetDbAsync();
        string[] collections = includeAll
            ? new[] { ActivityLogsCollection, SecurityAlertsCollection, IncidentReportsCollection, ProtectedAssetsCollection }
            : new[] { ActivityLogsCollection, SecurityAlertsCollection, IncidentReportsCollection };
        var summary = new StringBuilder();
        summary.AppendLine($"Firebase project: {_connection.ProjectId}");
        summary.AppendLine("Documents currently in the cloud:");

        foreach (string collectionName in collections)
        {
            int count = await CountCollectionDocumentsAsync(db.Collection(collectionName));
            summary.AppendLine($"  {collectionName}: {count}");
        }

        summary.AppendLine(includeAll
            ? "Keeping: security_settings (the Passkey PIN is never deleted here)"
            : "Keeping: protected_assets, security_settings (Passkey PIN)");

        return summary.ToString().TrimEnd();
    }

    public async Task<string> DeleteCloudDataAsync(bool includeAll)
    {
        FirestoreDb db = await _connection.GetDbAsync();
        string[] collections = includeAll
            ? new[] { ActivityLogsCollection, SecurityAlertsCollection, IncidentReportsCollection, ProtectedAssetsCollection }
            : new[] { ActivityLogsCollection, SecurityAlertsCollection, IncidentReportsCollection };
        int deletedTotal = 0;

        foreach (string collectionName in collections)
        {
            int deletedCollection = 0;
            CollectionReference collection = db.Collection(collectionName);
            while (true)
            {
                QuerySnapshot snapshot = await collection
                    .OrderBy(FieldPath.DocumentId)
                    .Select(FieldPath.DocumentId)
                    .Limit(400)
                    .GetSnapshotAsync();
                if (snapshot.Documents.Count == 0)
                {
                    break;
                }

                WriteBatch batch = db.StartBatch();
                foreach (DocumentSnapshot document in snapshot.Documents)
                {
                    batch.Delete(document.Reference);
                }
                await batch.CommitAsync();
                deletedCollection += snapshot.Documents.Count;
            }

            deletedTotal += deletedCollection;
        }

        return $"Deleted {deletedTotal} document(s) across {collections.Length} collection(s).";
    }

    private static async Task<int> CountCollectionDocumentsAsync(CollectionReference collection)
    {
        Query query = collection
            .OrderBy(FieldPath.DocumentId)
            .Select(FieldPath.DocumentId)
            .Limit(400);
        int count = 0;

        while (true)
        {
            QuerySnapshot snapshot = await query.GetSnapshotAsync();
            count += snapshot.Documents.Count;
            if (snapshot.Documents.Count < 400)
            {
                return count;
            }

            query = query.StartAfter(snapshot.Documents[^1]);
        }
    }

    // ------------------------------------------------------------------
    // MAPPING — field-name confidence, collection by collection, as of
    // Phase 7's inspection of the actual Monitoring Engine README:
    //
    //   protected_assets   CONFIRMED — matches the Monitoring Engine's own
    //                       documented JSON example ("Protected Asset data
    //                       structure") exactly: path, asset_name, asset_type,
    //                       classification, protected_at, protected_by, status.
    //                       Unchanged since Phase 3 — nothing to fix here.
    //
    //   activity_logs      CONFIRMED, with one real correction made this
    //                       phase: the field called "asset" IS the full path
    //                       (the Monitoring Engine's own README says so
    //                       explicitly — "the field called 'asset' has
    //                       always meant the full path"). There is no
    //                       separate "path" field. Phase 3 incorrectly
    //                       looked for a "path" field that doesn't exist,
    //                       so Path was silently always null — fixed below
    //                       by reading "asset" once and deriving a short
    //                       display name from it. user/device/action/
    //                       timestamp/risk_score/risk_level/risk_factors are
    //                       confirmed from the README's basic-record and
    //                       risk-scoring examples. "classification" as a
    //                       field name for activity_logs specifically is
    //                       still an inferred guess (not shown in a concrete
    //                       example) — flagged in case it needs correcting
    //                       once real classified activity data is available
    //                       to check against.
    //
    //   security_alerts    CONFIRMED — matches the Monitoring Engine's own
    //                       "Security alert data structure" example exactly:
    //                       alert_id, user, device, asset (short display
    //                       name), asset_path (full path), action,
    //                       asset_classification (NOT "classification" —
    //                       this was wrong in Phase 3/5), risk_score,
    //                       risk_level, risk_factors, timestamp (NOT
    //                       "created_at" — also wrong in Phase 3/5), status.
    //                       There is genuinely no "reason"/description field
    //                       in the real schema; Reason is synthesized from
    //                       risk_factors when the field is absent, so the
    //                       investigation UI still has something readable.
    //                       risk_factors is confirmed to be an array of
    //                       strings like "Confidential asset (+35)", not an
    //                       array of maps — GetRiskFactors now parses the
    //                       embedded weight out of that text instead of
    //                       discarding it.
    //
    //   incident_reports   DOES NOT EXIST YET. The Monitoring Engine's own
    //                       README lists Incident Reports as "Recommended
    //                       Phase 7" for ITS OWN roadmap — i.e. the engine
    //                       generates alerts but has no incident-management
    //                       write path at all yet. Querying this collection
    //                       is left in place (Firestore returns an empty
    //                       snapshot for a collection with zero documents,
    //                       not an error, so this stays safe), but it will
    //                       realistically always come back empty until the
    //                       Monitoring Engine actually writes to it. The
    //                       real source of incident data today remains the
    //                       local "Create Incident Report" action on the
    //                       Security Alerts page (Phase 5) — see
    //                       IncidentReportsViewModel.AddLocalIncident.
    //
    // Every Get*/Map* helper below tolerates a missing or mismatched field
    // (falls back to a safe default) rather than throwing. If a future
    // Monitoring Engine change shifts any of these field names again, this
    // is the only file that needs to change — no Model or ViewModel edits
    // required.
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

    private static ActivityLog MapActivityLog(DocumentSnapshot doc)
    {
        // Confirmed (Phase 7): "asset" IS the full path — there is no
        // separate "path" field in real activity_logs documents. Read it
        // once, use it as Path directly, and derive a short display name
        // for Asset. For a Rename, the Monitoring Engine puts the full
        // "old -> new" text in this same field — DeriveDisplayName leaves
        // that as-is rather than trying to shorten it.
        string assetFieldValue = GetString(doc, "asset", "unknown");

        return new ActivityLog
        {
            User = GetString(doc, "user", "unknown"),
            Device = GetString(doc, "device", "unknown"),
            Asset = DeriveDisplayName(assetFieldValue),
            Path = assetFieldValue,
            Action = GetEnum(doc, "action", ActionType.Open),
            Timestamp = GetTimestamp(doc, "timestamp"),
            Classification = GetEnum(doc, "classification", Classification.Public),
            RiskScore = GetNullableInt(doc, "risk_score"),
            RiskLevel = GetNullableEnum<RiskLevel>(doc, "risk_level"),
            RiskFactors = GetRiskFactors(doc),
            AssetPath = GetOptionalString(doc, "asset_path"),
            ProtectedRoot = GetOptionalString(doc, "protected_root"),
            Advisory = GetOptionalString(doc, "advisory"),
        };
    }

    private static SecurityAlert MapSecurityAlert(DocumentSnapshot doc)
    {
        // Confirmed (Phase 7) against the Monitoring Engine's own
        // "Security alert data structure" example: alert_id, asset (short
        // name), asset_path (full path — NOT "path"), asset_classification
        // (NOT "classification"), timestamp (NOT "created_at"). There is
        // genuinely no "reason"/description field in the real schema, so
        // one is synthesized from risk_factors when absent — still tries an
        // explicit "reason" field first in case a future engine version
        // adds one.
        List<RiskFactor> riskFactors = GetRiskFactors(doc);

        string? explicitReason = GetOptionalString(doc, "reason");
        string reason = !string.IsNullOrWhiteSpace(explicitReason)
            ? explicitReason
            : SynthesizeReason(riskFactors);

        return new SecurityAlert
        {
            AlertId = GetString(doc, "alert_id", doc.Id),
            User = GetString(doc, "user", "unknown"),
            Device = GetString(doc, "device", "unknown"),
            Asset = GetString(doc, "asset", "unknown"),
            Path = GetOptionalString(doc, "asset_path"),
            Action = GetEnum(doc, "action", ActionType.Open),
            Classification = GetEnum(doc, "asset_classification", Classification.Public),
            RiskScore = GetNullableInt(doc, "risk_score") ?? 0,
            RiskLevel = GetEnum(doc, "risk_level", RiskLevel.Low),
            Reason = reason,
            RiskFactors = riskFactors,
            Status = GetEnum(doc, "status", AlertStatus.New),
            CreatedAt = GetTimestamp(doc, "timestamp"),
        };
    }

    /// <summary>Builds a readable fallback reason from risk factor text, for the (confirmed-real) case where the alert document has no explicit reason field.</summary>
    private static string SynthesizeReason(List<RiskFactor> riskFactors)
        => riskFactors.Count > 0
            ? string.Join(", ", riskFactors.Select(f => f.Description)) + "."
            : "Flagged by the Risk Assessment Engine.";

    private static IncidentReport MapIncidentReport(DocumentSnapshot doc) => new()
    {
        IncidentId = GetString(doc, "incident_id", doc.Id),
        RelatedAlertId = GetOptionalString(doc, "related_alert_id"),
        User = GetString(doc, "user", "unknown"),
        Device = GetString(doc, "device", "unknown"),
        Asset = GetString(doc, "asset", "unknown"),
        Action = GetEnum(doc, "action", ActionType.Open),
        Classification = GetEnum(doc, "classification", Classification.Public),
        Timestamp = GetTimestamp(doc, "timestamp"),
        RiskLevel = GetEnum(doc, "risk_level", RiskLevel.Low),
        Description = GetString(doc, "description", string.Empty),
        InvestigationStatus = GetEnum(doc, "investigation_status", InvestigationStatus.New),
        Notes = GetOptionalString(doc, "investigation_notes") ?? GetOptionalString(doc, "notes"),
        Path = GetOptionalString(doc, "asset_path"),
        RiskScore = GetNullableInt(doc, "risk_score") ?? 0,
        Resolution = GetOptionalString(doc, "resolution"),
        DateResolved = GetNullableTimestamp(doc, "date_resolved"),
        CreatedAt = GetTimestamp(doc, "created_at"),
        UpdatedAt = GetTimestamp(doc, "updated_at"),
    };

    // --- field-read helpers: every one tolerates a missing/mismatched field ---
    //
    // Bug found in real usage (not caught by earlier testing, which only
    // ever used mock/sample data): calling doc.TryGetValue<T>(field, out T)
    // with a specific type — e.g. TryGetValue<Timestamp>(...) — can THROW
    // instead of returning false when the field exists but holds a
    // different type. The real Monitoring Engine writes protected_at,
    // timestamp, etc. as plain STRINGS (confirmed against a live document:
    // "protected_at": "2026-09-19 22:59:46"), not native Firestore
    // Timestamps — so asking for Timestamp directly blew up before the
    // string-parsing fallback below it ever ran, taking down every section
    // that loaded real data. Every helper below now reads the field once as
    // plain `object` (which always succeeds for any existing field) and
    // pattern-matches the actual runtime type in C#, instead of asking
    // Firestore's client to convert to a specific type up front.

    private static string GetString(DocumentSnapshot doc, string field, string fallback)
        => doc.TryGetValue(field, out object? raw) && raw is string s && !string.IsNullOrEmpty(s) ? s : fallback;

    private static string? GetOptionalString(DocumentSnapshot doc, string field)
        => doc.TryGetValue(field, out object? raw) && raw is string s ? s : null;

    private static TEnum GetEnum<TEnum>(DocumentSnapshot doc, string field, TEnum fallback) where TEnum : struct, Enum
        => doc.TryGetValue(field, out object? raw) && raw is string s && Enum.TryParse(s, ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;

    private static TEnum? GetNullableEnum<TEnum>(DocumentSnapshot doc, string field) where TEnum : struct, Enum
        => doc.TryGetValue(field, out object? raw) && raw is string s && Enum.TryParse(s, ignoreCase: true, out TEnum parsed)
            ? parsed
            : null;

    private static int? GetNullableInt(DocumentSnapshot doc, string field)
    {
        if (!doc.TryGetValue(field, out object? raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// Reads a timestamp field that may be either a native Firestore Timestamp
    /// or a plain string in "yyyy-MM-dd HH:mm:ss" form — confirmed (see note
    /// above) that the real Monitoring Engine actually uses the string form.
    /// Tries both; falls back to DateTime.MinValue — treated by the UI as
    /// "timestamp unavailable" — if neither works, rather than throwing.
    /// </summary>
    private static DateTime GetTimestamp(DocumentSnapshot doc, string field)
    {
        if (!doc.TryGetValue(field, out object? raw) || raw is null)
        {
            return DateTime.MinValue;
        }

        if (raw is Timestamp ts)
        {
            return ts.ToDateTime().ToLocalTime();
        }

        if (raw is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }

        return DateTime.MinValue;
    }

    /// <summary>Same as GetTimestamp, but returns null instead of DateTime.MinValue when the field is absent — for genuinely optional dates like "date_resolved" where "not yet resolved" is a real, meaningful state.</summary>
    private static DateTime? GetNullableTimestamp(DocumentSnapshot doc, string field)
    {
        if (!doc.TryGetValue(field, out object? raw) || raw is null)
        {
            return null;
        }

        if (raw is Timestamp ts)
        {
            return ts.ToDateTime().ToLocalTime();
        }

        if (raw is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Confirmed (Phase 7) against the Monitoring Engine's own risk_factors
    /// examples: this is an array of plain strings with the point value
    /// embedded in the text itself, e.g. "Confidential asset (+35)" — not
    /// an array of {description, weight} maps as Phase 3/5 defensively
    /// guessed. The map-shape handling below is kept purely as a forward-
    /// compatible fallback in case a future engine version changes this;
    /// the string branch is now the one that actually matters, and it
    /// parses the trailing "(+N)" out instead of discarding it.
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
                    if (description.Contains("has no configured risk weight", StringComparison.OrdinalIgnoreCase))
                    {
                        factors.Add(new RiskFactor { Description = "Create: no risk points", Weight = 0 });
                    }
                    else if (weight != 0 && !string.IsNullOrWhiteSpace(description))
                    {
                        factors.Add(new RiskFactor { Description = description, Weight = weight });
                    }
                    break;

                case string s when !string.IsNullOrWhiteSpace(s):
                    var parsed = ParseRiskFactorText(s);
                    if (parsed != null)
                    {
                        factors.Add(parsed);
                    }
                    break;
            }
        }

        return factors;
    }

    private static readonly Regex RiskFactorTextPattern = new(@"^(?<description>.*?)\s*\(\+?(?<weight>-?\d+)\)\s*$", RegexOptions.Compiled);

    /// <summary>Parses "Confidential asset (+35)" into Description="Confidential asset", Weight=35. Falls back to Weight=0 if the text doesn't match that shape.</summary>
    private static RiskFactor? ParseRiskFactorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.Contains("has no configured risk weight", StringComparison.OrdinalIgnoreCase))
        {
            return new RiskFactor { Description = "Create: no risk points", Weight = 0 };
        }

        Match match = RiskFactorTextPattern.Match(text);
        if (match.Success && int.TryParse(match.Groups["weight"].Value, out int weight))
        {
            if (weight == 0) return null;
            return new RiskFactor { Description = match.Groups["description"].Value, Weight = weight };
        }

        if (text.EndsWith("(+0)") || text.EndsWith("(0)")) return null;

        return new RiskFactor { Description = text, Weight = 0 };
    }

    /// <summary>
    /// Short display name from a full path (or, for a Rename, the "old -> new"
    /// text left untouched — shortening that would make it meaningless).
    /// Implemented as plain string slicing rather than System.IO.Path, since
    /// Path's separator handling is platform-dependent and these values are
    /// always Windows-style paths regardless of where this code runs.
    /// </summary>
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
}
