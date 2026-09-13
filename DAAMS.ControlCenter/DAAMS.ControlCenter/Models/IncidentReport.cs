using System;

namespace ControlCenter.Models;

/// <summary>
/// Manager-authored investigation record, optionally tied back to an alert/activity.
///
/// Phase 5 additions: Classification (for full Incident Details), Resolution
/// and DateResolved (documentation side of the workflow — what was found and
/// when it was closed out). InvestigationStatus/Notes/Resolution/UpdatedAt
/// are mutable so the investigation workflow can update them locally,
/// in-memory, pending real write-back once Firestore integration supports it
/// (see IncidentReportsViewModel).
/// </summary>
public class IncidentReport
{
    public required string IncidentId { get; init; }
    public string? RelatedAlertId { get; init; }
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }
    public ActionType Action { get; init; }
    public Classification Classification { get; init; }
    public DateTime Timestamp { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public required string Description { get; init; }

    /// <summary>Mutable — see IncidentReportsViewModel.ChangeStatusCommand.</summary>
    public InvestigationStatus InvestigationStatus { get; set; }

    /// <summary>Mutable — investigation notes can be added/updated as the review progresses.</summary>
    public string? Notes { get; set; }

    /// <summary>What was concluded once the investigation is closed out. Mutable, null until resolved.</summary>
    public string? Resolution { get; set; }

    /// <summary>When Resolution was recorded. Mutable, null until resolved.</summary>
    public DateTime? DateResolved { get; set; }

    public DateTime CreatedAt { get; init; }

    /// <summary>Mutable — bumped whenever status/notes/resolution change.</summary>
    public DateTime UpdatedAt { get; set; }
}
