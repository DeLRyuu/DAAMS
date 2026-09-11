using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using ControlCenter.Data;
using ControlCenter.Models;

namespace ControlCenter.ViewModels;

/// <summary>
/// Activity Logs: full detected-activity history for protected assets, as
/// reported by the DAAMS Monitoring System. This view only displays events —
/// it never invents or simulates them (see reference doc §9/§19).
/// </summary>
public class ActivityLogsViewModel : ViewModelBase
{
    public bool IsSampleData { get; } = true;

    private readonly ObservableCollection<ActivityLog> _allActivity;
    public ICollectionView Activity { get; }

    public int TotalCount => _allActivity.Count;

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

    public ActivityLogsViewModel()
    {
        _allActivity = new ObservableCollection<ActivityLog>(MockDataProvider.GetActivityLog());
        Activity = CollectionViewSource.GetDefaultView(_allActivity);
        Activity.Filter = FilterPredicate;

        _selectedAction = ActionOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];
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
