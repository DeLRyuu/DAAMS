using System;
using System.Collections.Generic;

namespace ControlCenter.Models;

/// <summary>
/// A High/Critical activity that has been surfaced for manager attention.
/// An alert is an investigation prompt, not a finding of wrongdoing —
/// see reference doc section 14/15 on interpretation language.
///
/// Phase 5 additions: Path and Classification (needed for the full Alert
/// Details view) and a mutable Status (so the investigation workflow can
/// move an alert through New → Investigating → Resolved/Dismissed locally,
/// in-memory, pending real write-back once Firestore integration supports
/// it — see SecurityAlertsViewModel).
///
/// Phase 7 addition: RiskFactors. Confirmed against the actual Monitoring
/// Engine source (alert_manager.py) — security_alerts documents carry the
/// same real, structured risk_factors array activity_logs does. Reason
/// remains a simple one-line summary (built from these same factors, since
/// there is no separate "reason" field in the real data — see
/// FirestoreService mapping notes); RiskFactors exposes the full list for
/// anywhere that wants the detailed breakdown, matching the "explainable
/// risk" principle the whole DAAMS system is built around.
/// </summary>
public class SecurityAlert
{
    public required string AlertId { get; init; }
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }

    /// <summary>Full path of the affected asset. Nullable — not every source will have it.</summary>
    public string? Path { get; init; }

    public ActionType Action { get; init; }
    public Classification Classification { get; init; }
    public int RiskScore { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public required string Reason { get; init; }

    /// <summary>The full, structured list of risk contributors behind this alert's score (e.g. "Confidential asset (+35)"). See Reason for a one-line summary of the same data.</summary>
    public List<RiskFactor> RiskFactors { get; init; } = new();

    /// <summary>Mutable so the investigation workflow can change it locally (see SecurityAlertsViewModel.ChangeStatusCommand).</summary>
    public AlertStatus Status { get; set; }

    public DateTime CreatedAt { get; init; }
}
