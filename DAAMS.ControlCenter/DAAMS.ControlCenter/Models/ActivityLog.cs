using System;
using System.Collections.Generic;

namespace ControlCenter.Models;

/// <summary>
/// A single detected activity event on a protected asset, as reported by the
/// DAAMS Monitoring System. RiskScore/RiskLevel/RiskFactors are optional/nullable
/// because not every build of the Monitoring System has risk assessment wired up yet
/// (see reference doc section 21, "Current Development Boundary").
///
/// PHASE 7: added Advisory. Confirmed against the actual Monitoring Engine
/// source (risk_engine.py) — every risk-assessed activity_logs document has
/// a real "advisory" field (a plain-language investigation note for
/// High/Critical results, null otherwise) that was previously not mapped
/// to anything and silently dropped.
/// </summary>
public class ActivityLog
{
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }
    public string? Path { get; init; }
    public ActionType Action { get; init; }
    public DateTime Timestamp { get; init; }
    public Classification Classification { get; init; }

    /// <summary>Null when risk assessment has not been performed/received for this event.</summary>
    public int? RiskScore { get; init; }

    /// <summary>Null when risk assessment has not been performed/received for this event.</summary>
    public RiskLevel? RiskLevel { get; init; }

    public List<RiskFactor> RiskFactors { get; init; } = new();

    /// <summary>
    /// Plain-language investigation note the Monitoring Engine attaches to
    /// High/Critical activity (e.g. "Suspicious activity requiring
    /// investigation..."). Null for Low/Medium activity, or when risk
    /// assessment hasn't been performed.
    /// </summary>
    public string? Advisory { get; init; }

    // Display-only properties
    public DateTime Date => Timestamp.Date;
    public int RepeatCount { get; set; } = 1;
    public bool HasRepeats => RepeatCount > 1;
    public string RepeatCountDisplay => RepeatCount > 1 ? $" ×{RepeatCount}" : string.Empty;
    public List<DateTime> MergedTimestamps { get; set; } = new();
    public string? MatchedAlertId { get; set; }
    public bool HasMatchingCase => !string.IsNullOrEmpty(MatchedAlertId);
}
