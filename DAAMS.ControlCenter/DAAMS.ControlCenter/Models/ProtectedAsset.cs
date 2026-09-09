using System;

namespace ControlCenter.Models;

/// <summary>
/// Represents a file or folder registered with DAAMS protection.
/// Field set mirrors monitoring_engine/protected_assets.json exactly
/// so this model maps cleanly onto the future Firestore document.
/// </summary>
public class ProtectedAsset
{
    public required string Path { get; init; }
    public required string AssetName { get; init; }
    public AssetType AssetType { get; init; }
    public Classification Classification { get; init; }
    public DateTime ProtectedAt { get; init; }
    public required string ProtectedBy { get; init; }
    public ProtectionStatus Status { get; init; }
}
