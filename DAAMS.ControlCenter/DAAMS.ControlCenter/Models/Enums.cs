namespace ControlCenter.Models;

/// <summary>
/// Asset sensitivity classification.
/// Names and weights must stay in sync with the DAAMS Monitoring System reference.
/// Do not add classification levels here without updating the Monitoring System.
/// </summary>
public enum Classification
{
    Public,
    Sensitive,
    Private,
    Confidential
}

/// <summary>
/// Type of protected asset.
/// </summary>
public enum AssetType
{
    File,
    Folder
}

/// <summary>
/// Whether a protected asset is currently protected.
/// Removing protection changes this status only — it never deletes the
/// underlying file/folder or the registry record.
/// </summary>
public enum ProtectionStatus
{
    Protected,
    Unprotected
}

/// <summary>
/// DAAMS action model. This is the INTENDED full set from the reference doc.
/// The Monitoring System currently only reliably detects Create/Modify/Rename/Delete;
/// the Control Center must not invent or simulate Open/Copy/Move events that were
/// not actually reported by the Monitoring System.
/// </summary>
public enum ActionType
{
    Open,
    Modify,
    Copy,
    Move,
    Rename,
    Delete
}

/// <summary>
/// Risk level bands as defined by the Monitoring/Risk Assessment Engine.
/// 0–24 Low, 25–49 Medium, 50–74 High, 75+ Critical.
/// The Control Center displays these bands; it does not calculate them.
/// </summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Lifecycle state of a Security Alert as it moves through manager review.
/// </summary>
public enum AlertStatus
{
    New,
    Reviewed,
    Investigating,
    Resolved
}

/// <summary>
/// Investigation lifecycle for an Incident Report.
/// </summary>
public enum InvestigationStatus
{
    New,
    UnderInvestigation,
    Resolved,
    Dismissed
}
