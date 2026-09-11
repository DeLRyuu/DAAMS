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
/// Security Alerts: High/Critical activity surfaced for manager review. An alert
/// is a prompt for investigation — not a finding of wrongdoing (see reference
/// doc §14/§15). This view is read-only presentation; alert generation itself
/// belongs to the Monitoring/Risk Assessment Engine, not this app.
/// </summary>
public class SecurityAlertsViewModel : ViewModelBase
{
    public bool IsSampleData { get; } = true;

    private readonly ObservableCollection<SecurityAlert> _allAlerts;
    public ICollectionView Alerts { get; }

    public int TotalCount => _allAlerts.Count;
    public int NeedsAttentionCount => _allAlerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Alerts.Refresh();
            }
        }
    }

    public List<FilterOption<AlertStatus>> StatusOptions { get; } = FilterOption<AlertStatus>.AllOptions("All Statuses");

    private FilterOption<AlertStatus> _selectedStatus;
    public FilterOption<AlertStatus> SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                Alerts.Refresh();
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
                Alerts.Refresh();
            }
        }
    }

    public SecurityAlertsViewModel()
    {
        _allAlerts = new ObservableCollection<SecurityAlert>(
            MockDataProvider.GetSecurityAlerts().OrderByDescending(a => a.CreatedAt));
        Alerts = CollectionViewSource.GetDefaultView(_allAlerts);
        Alerts.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not SecurityAlert alert)
        {
            return false;
        }

        if (SelectedStatus.Value is AlertStatus status && alert.Status != status)
        {
            return false;
        }

        if (SelectedRiskLevel.Value is RiskLevel riskLevel && alert.RiskLevel != riskLevel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = alert.User.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.Device.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.Asset.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.AlertId.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
