using System;

namespace ControlCenter.Models;

/// <summary>
/// A High/Critical activity that has been surfaced for manager attention.
/// An alert is an investigation prompt, not a finding of wrongdoing —
/// see reference doc section 14/15 on interpretation language.
/// </summary>
public class SecurityAlert
{
    public required string AlertId { get; init; }
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }
    public ActionType Action { get; init; }
    public int RiskScore { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public required string Reason { get; init; }
    public AlertStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
}
