using System;
using System.Collections.Generic;

namespace ControlCenter.Models;

/// <summary>
/// A single detected activity event on a protected asset, as reported by the
/// DAAMS Monitoring System. RiskScore/RiskLevel/RiskFactors are optional/nullable
/// because not every build of the Monitoring System has risk assessment wired up yet
/// (see reference doc section 21, "Current Development Boundary").
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
}
