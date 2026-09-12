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
/// Security Alerts: High/Critical activity surfaced for manager review, read
/// from Firestore's security_alerts collection. An alert is a prompt for
/// investigation — not a finding of wrongdoing (reference doc §14/§15). This
/// view is read-only; alert generation belongs to the Monitoring/Risk
/// Assessment Engine, not this app.
/// </summary>
public class SecurityAlertsViewModel : DataSectionViewModelBase
{
    private readonly FirestoreService _firestore;
    private readonly ObservableCollection<SecurityAlert> _allAlerts = new();

    public ICollectionView Alerts { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private int _needsAttentionCount;
    public int NeedsAttentionCount
    {
        get => _needsAttentionCount;
        private set => SetProperty(ref _needsAttentionCount, value);
    }

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

    public SecurityAlertsViewModel(FirestoreService firestore)
    {
        _firestore = firestore;

        Alerts = CollectionViewSource.GetDefaultView(_allAlerts);
        Alerts.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<SecurityAlert> alerts = await _firestore.GetSecurityAlertsAsync();
        Apply(alerts);
        return alerts.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetSecurityAlerts());

    private void Apply(List<SecurityAlert> alerts)
    {
        _allAlerts.Clear();
        foreach (SecurityAlert alert in alerts.OrderByDescending(a => a.CreatedAt))
        {
            _allAlerts.Add(alert);
        }

        TotalCount = _allAlerts.Count;
        NeedsAttentionCount = _allAlerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);
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
