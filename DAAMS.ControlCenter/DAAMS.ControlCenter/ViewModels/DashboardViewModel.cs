using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ControlCenter.Data;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Dashboard: one primary insight, a row of secondary metrics, risk
/// distribution, then recent activity / recent alerts — all computed from
/// Firestore data loaded across the four collections in parallel.
///
/// Doesn't inherit DataSectionViewModelBase because its shape is different
/// (four parallel loads feeding several metrics, not one filtered
/// collection), but implements the same IDataSection contract and the same
/// Loading/Loaded/Empty/Error state machine for a consistent feel.
/// </summary>
public class DashboardViewModel : ViewModelBase, IDataSection
{
    private readonly FirestoreService _firestore;
    private bool _hasLoadedOnce;

    private DataLoadState _loadState = DataLoadState.Loading;
    public DataLoadState LoadState
    {
        get => _loadState;
        private set => SetProperty(ref _loadState, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private bool _isShowingSampleFallback;
    public bool IsShowingSampleFallback
    {
        get => _isShowingSampleFallback;
        private set => SetProperty(ref _isShowingSampleFallback, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ShowSampleDataCommand { get; }

    // --- Primary insight -------------------------------------------------
    private int _highRiskActivities;
    public int HighRiskActivities
    {
        get => _highRiskActivities;
        private set => SetProperty(ref _highRiskActivities, value);
    }

    // --- Secondary metrics -------------------------------------------------
    private int _totalProtectedAssets;
    public int TotalProtectedAssets
    {
        get => _totalProtectedAssets;
        private set => SetProperty(ref _totalProtectedAssets, value);
    }

    private int _totalActivityLogs;
    public int TotalActivityLogs
    {
        get => _totalActivityLogs;
        private set => SetProperty(ref _totalActivityLogs, value);
    }

    private int _activeSecurityAlerts;
    public int ActiveSecurityAlerts
    {
        get => _activeSecurityAlerts;
        private set => SetProperty(ref _activeSecurityAlerts, value);
    }

    private int _openIncidents;
    public int OpenIncidents
    {
        get => _openIncidents;
        private set => SetProperty(ref _openIncidents, value);
    }

    // --- Risk distribution (computed client-side from loaded activity) -----
    private int _riskLowCount;
    public int RiskLowCount
    {
        get => _riskLowCount;
        private set => SetProperty(ref _riskLowCount, value);
    }

    private int _riskMediumCount;
    public int RiskMediumCount
    {
        get => _riskMediumCount;
        private set => SetProperty(ref _riskMediumCount, value);
    }

    private int _riskHighCount;
    public int RiskHighCount
    {
        get => _riskHighCount;
        private set => SetProperty(ref _riskHighCount, value);
    }

    private int _riskCriticalCount;
    public int RiskCriticalCount
    {
        get => _riskCriticalCount;
        private set => SetProperty(ref _riskCriticalCount, value);
    }

    private int _riskTotalCount;
    public int RiskTotalCount
    {
        get => _riskTotalCount;
        private set => SetProperty(ref _riskTotalCount, value);
    }

    // --- Lists -------------------------------------------------
    public ObservableCollection<ActivityLog> RecentActivity { get; } = new();
    public ObservableCollection<SecurityAlert> RecentAlerts { get; } = new();

    public DashboardViewModel(FirestoreService firestore)
    {
        _firestore = firestore;
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        ShowSampleDataCommand = new RelayCommand(_ => LoadSampleFallback());
    }

    public void EnsureLoaded()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        LoadState = DataLoadState.Loading;
        ErrorMessage = null;
        IsShowingSampleFallback = false;

        try
        {
            Task<List<ProtectedAsset>> assetsTask = _firestore.GetProtectedAssetsAsync();
            Task<List<ActivityLog>> activityTask = _firestore.GetActivityLogsAsync();
            Task<List<SecurityAlert>> alertsTask = _firestore.GetSecurityAlertsAsync();
            Task<List<IncidentReport>> incidentsTask = _firestore.GetIncidentReportsAsync();

            await Task.WhenAll(assetsTask, activityTask, alertsTask, incidentsTask);

            List<ProtectedAsset> assets = assetsTask.Result;
            List<ActivityLog> activity = activityTask.Result;
            List<SecurityAlert> alerts = alertsTask.Result;
            List<IncidentReport> incidents = incidentsTask.Result;

            Apply(assets, activity, alerts, incidents);

            int total = assets.Count + activity.Count + alerts.Count + incidents.Count;
            LoadState = total > 0 ? DataLoadState.Loaded : DataLoadState.Empty;
        }
        catch (Exception ex)
        {
            LoadState = DataLoadState.Error;
            ErrorMessage = "Unable to load data from Firestore. Check your connection and Firestore configuration.";
            Debug.WriteLine($"[Dashboard] Firestore load failed: {ex}");
        }
    }

    private void LoadSampleFallback()
    {
        Apply(
            MockDataProvider.GetProtectedAssets(),
            MockDataProvider.GetActivityLog(),
            MockDataProvider.GetSecurityAlerts(),
            MockDataProvider.GetIncidentReports());

        IsShowingSampleFallback = true;
        ErrorMessage = null;
        LoadState = DataLoadState.Loaded;
    }

    private void Apply(List<ProtectedAsset> assets, List<ActivityLog> activity, List<SecurityAlert> alerts, List<IncidentReport> incidents)
    {
        TotalProtectedAssets = assets.Count(a => a.Status == ProtectionStatus.Protected);
        TotalActivityLogs = activity.Count;
        ActiveSecurityAlerts = alerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);
        OpenIncidents = incidents.Count(i => i.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.UnderInvestigation);

        RiskLowCount = activity.Count(a => a.RiskLevel == RiskLevel.Low);
        RiskMediumCount = activity.Count(a => a.RiskLevel == RiskLevel.Medium);
        RiskHighCount = activity.Count(a => a.RiskLevel == RiskLevel.High);
        RiskCriticalCount = activity.Count(a => a.RiskLevel == RiskLevel.Critical);
        RiskTotalCount = RiskLowCount + RiskMediumCount + RiskHighCount + RiskCriticalCount;
        HighRiskActivities = RiskHighCount + RiskCriticalCount;

        RecentActivity.Clear();
        foreach (ActivityLog entry in activity.OrderByDescending(a => a.Timestamp).Take(5))
        {
            RecentActivity.Add(entry);
        }

        RecentAlerts.Clear();
        foreach (SecurityAlert alert in alerts.OrderByDescending(a => a.CreatedAt).Take(3))
        {
            RecentAlerts.Add(alert);
        }
    }
}
