using System;
using System.Collections.Generic;
using ControlCenter.ViewModels;

namespace ControlCenter.Models;

public class CaseItem : ViewModelBase
{
    public required string AlertId { get; init; }
    public string? IncidentId { get; set; }
    public required string User { get; init; }
    public required string Device { get; init; }
    public required string Asset { get; init; }
    public string? Path { get; init; }
    public ActionType Action { get; init; }
    public Classification Classification { get; init; }
    public int RiskScore { get; init; }
    public RiskLevel RiskLevel { get; init; }
    public required string Reason { get; init; }
    public List<RiskFactor> RiskFactors { get; init; } = new();
    public DateTime DetectedAt { get; init; }

    private CaseStatus _status;
    public CaseStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(Group));
                OnPropertyChanged(nameof(GroupOrder));
            }
        }
    }

    public void RefreshStatusBinding() => OnPropertyChanged(nameof(Status));

    private string? _notes;
    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    private string? _resolution;
    public string? Resolution
    {
        get => _resolution;
        set => SetProperty(ref _resolution, value);
    }

    private DateTime? _dateResolved;
    public DateTime? DateResolved
    {
        get => _dateResolved;
        set
        {
            if (SetProperty(ref _dateResolved, value))
            {
                OnPropertyChanged(nameof(DateResolvedText));
            }
        }
    }

    public string DateResolvedText
    {
        get
        {
            if (DateResolved == null) return string.Empty;
            if (DateResolved.Value < DetectedAt) return "-";
            return DateResolved.Value.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    private DateTime _updatedAt;
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    public string Group => Status switch
    {
        CaseStatus.New => "Needs review",
        CaseStatus.Investigating => "In progress",
        CaseStatus.Resolved => "Closed",
        CaseStatus.Dismissed => "Closed",
        _ => "Closed"
    };

    public int GroupOrder => Status switch
    {
        CaseStatus.New => 0,
        CaseStatus.Investigating => 1,
        _ => 2
    };

    public string AgeText
    {
        get
        {
            var date = DetectedAt.Date;
            var today = DateTime.Today;
            if (date == today)
            {
                return $"today at {DetectedAt:HH:mm}";
            }
            if (date == today.AddDays(-1))
            {
                return $"yesterday at {DetectedAt:HH:mm}";
            }
            int days = (today - date).Days;
            if (days > 1)
            {
                return $"{days} days ago";
            }
            return $"{DetectedAt:yyyy-MM-dd HH:mm}";
        }
    }
}
