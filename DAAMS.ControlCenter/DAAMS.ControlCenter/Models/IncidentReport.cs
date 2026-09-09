using System;

namespace ControlCenter.Models;

/// <summary>
/// Manager-authored investigation record, optionally tied back to an alert/activity.
/// </summary>
public class IncidentReport
{
    public required string IncidentId { get; init; }
    public string? RelatedAlertId { get; init; }
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }
    public ActionType Action { get; init; }
    public DateTime Timestamp { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public required string Description { get; init; }
    public InvestigationStatus InvestigationStatus { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
