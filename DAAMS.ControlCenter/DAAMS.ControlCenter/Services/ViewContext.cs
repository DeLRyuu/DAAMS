using System;
using ControlCenter.ViewModels;

namespace ControlCenter.Services;

public enum TimeRange
{
    Today,
    Last7Days,
    Last30Days,
    All
}

public class ViewContext : ViewModelBase
{
    public static ViewContext Current { get; } = new();

    public event Action? RangeChanged;

    private TimeRange _range = TimeRange.All;
    public TimeRange Range
    {
        get => _range;
        set
        {
            if (SetProperty(ref _range, value))
            {
                OnPropertyChanged(nameof(From));
                RangeChanged?.Invoke();
            }
        }
    }

    public DateTime? From => Range switch
    {
        TimeRange.Today => DateTime.Today,
        TimeRange.Last7Days => DateTime.Now.AddDays(-7),
        TimeRange.Last30Days => DateTime.Now.AddDays(-30),
        TimeRange.All => null,
        _ => null
    };

    public bool IsInRange(DateTime t) => From is null || t >= From.Value;

    private DateTime? _lastUpdated;
    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (SetProperty(ref _lastUpdated, value))
            {
                OnPropertyChanged(nameof(LastUpdatedText));
            }
        }
    }

    public string LastUpdatedText => _lastUpdated.HasValue
        ? $"Updated {_lastUpdated.Value:HH:mm:ss}"
        : string.Empty;

    private string? _pendingSearch;
    public string? PendingSearch
    {
        get => _pendingSearch;
        set => SetProperty(ref _pendingSearch, value);
    }

    public string? TakePendingSearch()
    {
        string? search = _pendingSearch;
        PendingSearch = null;
        return search;
    }

    public void Reset()
    {
        _range = TimeRange.All;
        _lastUpdated = null;
        _pendingSearch = null;
        RangeChanged = null;
        OnPropertyChanged(nameof(Range));
        OnPropertyChanged(nameof(From));
        OnPropertyChanged(nameof(LastUpdated));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(PendingSearch));
    }
}
