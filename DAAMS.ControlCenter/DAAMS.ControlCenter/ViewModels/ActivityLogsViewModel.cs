using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using ControlCenter.Data;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Activity Logs: detected-activity history for protected assets, read from
/// Firestore's activity_logs collection, as reported by the DAAMS Monitoring
/// System. This view only displays events — it never invents or simulates
/// them (reference doc §9/§19).
/// </summary>
public class ActivityLogsViewModel : DataSectionViewModelBase
{
    private readonly FirestoreService _firestore;
    private readonly ObservableCollection<ActivityLog> _allActivity = new();

    public ICollectionView Activity { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Activity.Refresh();
            }
        }
    }

    public List<FilterOption<ActionType>> ActionOptions { get; } = FilterOption<ActionType>.AllOptions("All Actions");

    private FilterOption<ActionType> _selectedAction;
    public FilterOption<ActionType> SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (SetProperty(ref _selectedAction, value))
            {
                Activity.Refresh();
            }
        }
    }

    public List<FilterOption<RiskLevel>> RiskLevelOptions { get; } = FilterOption<RiskLevel>.AllOptions("All Risk Levels");

    private FilterOption<RiskLevel> _selectedRiskLevel;
    public FilterOption<RiskLevel> SelectedRiskLevel
    {
        get => _selectedRiskLevel;
        set
        {
            if (SetProperty(ref _selectedRiskLevel, value))
            {
                Activity.Refresh();
            }
        }
    }

    public ActivityLogsViewModel(FirestoreService firestore)
    {
        _firestore = firestore;

        Activity = CollectionViewSource.GetDefaultView(_allActivity);
        Activity.Filter = FilterPredicate;

        _selectedAction = ActionOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<ActivityLog> activity = await _firestore.GetActivityLogsAsync();
        Apply(activity);
        return activity.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetActivityLog());

    private void Apply(List<ActivityLog> activity)
    {
        _allActivity.Clear();
        foreach (ActivityLog entry in activity.OrderByDescending(a => a.Timestamp))
        {
            _allActivity.Add(entry);
        }

        TotalCount = _allActivity.Count;
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not ActivityLog entry)
        {
            return false;
        }

        if (SelectedAction.Value is ActionType action && entry.Action != action)
        {
            return false;
        }

        if (SelectedRiskLevel.Value is RiskLevel riskLevel && entry.RiskLevel != riskLevel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = entry.User.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || entry.Device.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || entry.Asset.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
