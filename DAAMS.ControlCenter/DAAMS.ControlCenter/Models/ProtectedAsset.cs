using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ControlCenter.Models;

/// <summary>
/// Represents a file or folder registered with DAAMS protection.
/// Field set mirrors monitoring_engine/protected_assets.json exactly
/// so this model maps cleanly onto the future Firestore document.
/// </summary>
public class ProtectedAsset : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public required string AssetName { get; init; }
    public AssetType AssetType { get; init; }
    public Classification Classification { get; init; }
    public DateTime ProtectedAt { get; init; }
    public required string ProtectedBy { get; init; }
    public ProtectionStatus Status { get; init; }

    // Display-only properties (not persisted)
    private DateTime? _lastActivity;
    public DateTime? LastActivity
    {
        get => _lastActivity;
        set
        {
            if (_lastActivity != value)
            {
                _lastActivity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNoRecentActivity));
            }
        }
    }

    private string _lastActivityText = "—";
    public string LastActivityText
    {
        get => _lastActivityText;
        set
        {
            if (_lastActivityText != value)
            {
                _lastActivityText = value;
                OnPropertyChanged();
            }
        }
    }

    private int _eventsInRange;
    public int EventsInRange
    {
        get => _eventsInRange;
        set
        {
            if (_eventsInRange != value)
            {
                _eventsInRange = value;
                OnPropertyChanged();
            }
        }
    }

    private int _openCases;
    public int OpenCases
    {
        get => _openCases;
        set
        {
            if (_openCases != value)
            {
                _openCases = value;
                OnPropertyChanged();
            }
        }
    }

    private RiskLevel? _highestRisk;
    public RiskLevel? HighestRisk
    {
        get => _highestRisk;
        set
        {
            if (_highestRisk != value)
            {
                _highestRisk = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HighestRiskText));
            }
        }
    }

    public string HighestRiskText => HighestRisk.HasValue ? HighestRisk.Value.ToString() : "None";

    public bool HasNoRecentActivity
    {
        get
        {
            if (!LastActivity.HasValue)
            {
                return true;
            }
            DateTime now = LastActivity.Value.Kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now;
            return (now - LastActivity.Value).TotalDays > 30;
        }
    }

    public int BaseRiskWeight => Classification switch
    {
        Classification.Public => 5,
        Classification.Sensitive => 15,
        Classification.Private => 25,
        Classification.Confidential => 35,
        _ => 0
    };

    public string BaseRiskText => $"base risk +{BaseRiskWeight}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
