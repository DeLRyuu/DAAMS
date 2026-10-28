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

public class DashboardViewModel : ViewModelBase, IDataSection
{
    private readonly LocalDatabaseService _db;
    private readonly Action<NavigationSection, string> _navigateWith;
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

    private string? _actionError;
    public string? ActionError
    {
        get => _actionError;
        private set => SetProperty(ref _actionError, value);
    }

    private bool _isShowingSampleFallback;
    public bool IsShowingSampleFallback
    {
        get => _isShowingSampleFallback;
        private set => SetProperty(ref _isShowingSampleFallback, value);
    }

    private int _needsReviewCount;
    public int NeedsReviewCount
    {
        get => _needsReviewCount;
        private set => SetProperty(ref _needsReviewCount, value);
    }

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

    private int _openCases;
    public int OpenCases
    {
        get => _openCases;
        private set => SetProperty(ref _openCases, value);
    }

    private int _devicesReportingActivity;
    public int DevicesReportingActivity
    {
        get => _devicesReportingActivity;
        private set => SetProperty(ref _devicesReportingActivity, value);
    }

    private string _rangeLabel = "7 days";
    public string RangeLabel
    {
        get => _rangeLabel;
        private set => SetProperty(ref _rangeLabel, value);
    }

    private bool _showThirtyDayHourRange;
    public bool ShowThirtyDayHourRange
    {
        get => _showThirtyDayHourRange;
        private set => SetProperty(ref _showThirtyDayHourRange, value);
    }

    public int RiskLowCount { get; private set; }
    public int RiskMediumCount { get; private set; }
    public int RiskHighCount { get; private set; }
    public int RiskCriticalCount { get; private set; }

    public ObservableCollection<CaseItem> NeedsReviewCases { get; } = new();
    public ObservableCollection<ActivityLog> RecentActivity { get; } = new();
    public ObservableCollection<DeviceSummary> DevicesNeedingAttention { get; } = new();
    public ObservableCollection<DashboardHourBar> ActivityByHour { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand ShowSampleDataCommand { get; }
    public ICommand OpenCaseCommand { get; }
    public ICommand OpenDeviceCommand { get; }

    public DashboardViewModel(LocalDatabaseService db, Action<NavigationSection, string> navigateWith)
    {
        _db = db;
        _navigateWith = navigateWith;
        RefreshCommand = RelayCommand.FromAsync(_ => RefreshAsync(), ex => ActionError = ex.Message);
        ShowSampleDataCommand = new RelayCommand(_ => LoadSampleFallback());
        OpenCaseCommand = new RelayCommand(param =>
        {
            if (param is CaseItem item)
            {
                _navigateWith(NavigationSection.SecurityAlerts, item.AlertId);
            }
        });
        OpenDeviceCommand = new RelayCommand(param =>
        {
            if (param is DeviceSummary device)
            {
                _navigateWith(NavigationSection.Devices, device.Name);
            }
        });
        ViewContext.Current.RangeChanged += () => _ = RefreshAsync();
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
        if (LoadState != DataLoadState.Loaded)
        {
            LoadState = DataLoadState.Loading;
        }
        ErrorMessage = null;
        IsShowingSampleFallback = false;

        try
        {
            Task<System.Collections.Generic.List<CaseItem>> casesTask = _db.GetCasesAsync();
            Task<System.Collections.Generic.List<ActivityLog>> activityTask = _db.GetActivityLogsAsync();
            Task<System.Collections.Generic.List<ProtectedAsset>> assetsTask = _db.GetProtectedAssetsAsync();

            await Task.WhenAll(casesTask, activityTask, assetsTask);

            Apply(casesTask.Result, activityTask.Result, assetsTask.Result);
            int total = casesTask.Result.Count + activityTask.Result.Count + assetsTask.Result.Count;
            LoadState = total > 0 ? DataLoadState.Loaded : DataLoadState.Empty;
            ViewContext.Current.LastUpdated = DateTime.Now;

            var search = ViewContext.Current.TakePendingSearch();
            if (search != null)
            {
                ApplyPendingSearch(search);
            }
        }
        catch (Exception ex)
        {
            LoadState = DataLoadState.Error;
            ErrorMessage = "Unable to load data from the local database.";
            Debug.WriteLine($"[Dashboard] Local database load failed: {ex}");
        }
    }

    protected virtual void ApplyPendingSearch(string search)
    {
    }

    private void LoadSampleFallback()
    {
        var cases = MockDataProvider.GetSecurityAlerts().Select(alert => new CaseItem
        {
            AlertId = alert.AlertId,
            User = alert.User,
            Device = alert.Device,
            Asset = alert.Asset,
            Path = alert.Path,
            Action = alert.Action,
            Classification = alert.Classification,
            RiskScore = alert.RiskScore,
            RiskLevel = alert.RiskLevel,
            Reason = alert.Reason,
            RiskFactors = alert.RiskFactors,
            DetectedAt = alert.CreatedAt,
            UpdatedAt = alert.CreatedAt,
            Status = alert.Status switch
            {
                AlertStatus.Investigating => CaseStatus.Investigating,
                AlertStatus.Resolved => CaseStatus.Resolved,
                AlertStatus.Reviewed => CaseStatus.Dismissed,
                _ => CaseStatus.New
            }
        }).ToList();

        Apply(cases, MockDataProvider.GetActivityLog(), MockDataProvider.GetProtectedAssets());
        IsShowingSampleFallback = true;
        ErrorMessage = null;
        LoadState = DataLoadState.Loaded;
    }

    private void Apply(
        System.Collections.Generic.List<CaseItem> cases,
        System.Collections.Generic.List<ActivityLog> activity,
        System.Collections.Generic.List<ProtectedAsset> assets)
    {
        DateTime now = DateTime.Now;
        string from = ViewContext.Current.Range switch
        {
            TimeRange.Today => "today",
            TimeRange.Last7Days => "7 days",
            TimeRange.Last30Days => "30 days",
            _ => "all time"
        };
        RangeLabel = from;
        ShowThirtyDayHourRange = ViewContext.Current.Range == TimeRange.All;

        var rangedActivity = activity
            .Where(entry => ViewContext.Current.IsInRange(entry.Timestamp))
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
        var rangedCases = cases
            .Where(item => ViewContext.Current.IsInRange(item.DetectedAt))
            .ToList();

        TotalProtectedAssets = assets.Count(asset => asset.Status == ProtectionStatus.Protected);
        TotalActivityLogs = rangedActivity.Count;
        NeedsReviewCount = rangedCases.Count(item => item.Status == CaseStatus.New);
        OpenCases = rangedCases.Count(item => item.Status is CaseStatus.New or CaseStatus.Investigating);

        NeedsReviewCases.Clear();
        foreach (CaseItem item in rangedCases
                     .Where(item => item.Status is CaseStatus.New or CaseStatus.Investigating)
                     .OrderBy(item => item.Status == CaseStatus.New ? 0 : 1)
                     .ThenByDescending(item => item.RiskScore)
                     .ThenByDescending(item => item.DetectedAt)
                     .Take(3))
        {
            NeedsReviewCases.Add(item);
        }

        RecentActivity.Clear();
        foreach (ActivityLog entry in rangedActivity.Take(8))
        {
            RecentActivity.Add(entry);
        }

        RiskLowCount = rangedActivity.Count(entry => entry.RiskLevel == RiskLevel.Low);
        RiskMediumCount = rangedActivity.Count(entry => entry.RiskLevel == RiskLevel.Medium);
        RiskHighCount = rangedActivity.Count(entry => entry.RiskLevel == RiskLevel.High);
        RiskCriticalCount = rangedActivity.Count(entry => entry.RiskLevel == RiskLevel.Critical);
        OnPropertyChanged(nameof(RiskLowCount));
        OnPropertyChanged(nameof(RiskMediumCount));
        OnPropertyChanged(nameof(RiskHighCount));
        OnPropertyChanged(nameof(RiskCriticalCount));

        var deviceSummaries = DeviceService.Build(activity, cases);
        var deviceNamesInRange = rangedActivity
            .Select(entry => entry.Device)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DevicesReportingActivity = deviceNamesInRange.Count;

        DevicesNeedingAttention.Clear();
        foreach (DeviceSummary device in deviceSummaries
                     .Where(device => device.OpenCases > 0)
                     .OrderByDescending(device => device.OpenCases)
                     .ThenByDescending(device => device.HighestRisk)
                     .Take(3))
        {
            DevicesNeedingAttention.Add(device);
        }

        var hourActivity = ViewContext.Current.Range == TimeRange.All
            ? rangedActivity.Where(entry => entry.Timestamp >= now.AddDays(-30)).ToList()
            : rangedActivity;
        int[] hourCounts = new int[24];
        foreach (ActivityLog entry in hourActivity)
        {
            hourCounts[entry.Timestamp.Hour]++;
        }

        int maxHourCount = hourCounts.Max();
        ActivityByHour.Clear();
        for (int hour = 0; hour < hourCounts.Length; hour++)
        {
            ActivityByHour.Add(new DashboardHourBar
            {
                Hour = hour,
                Label = hour % 6 == 0 ? hour.ToString("00") : string.Empty,
                Count = hourCounts[hour],
                HeightPx = maxHourCount == 0 ? 4 : Math.Max(4, hourCounts[hour] * 48d / maxHourCount),
                IsWorkHour = hour is >= 7 and <= 18
            });
        }
    }

}

public class DashboardHourBar
{
    public int Hour { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public double HeightPx { get; init; }
    public bool IsWorkHour { get; init; }
}
