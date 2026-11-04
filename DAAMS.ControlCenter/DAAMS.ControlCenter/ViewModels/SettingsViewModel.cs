using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Settings: monitoring/risk/notification/system preferences, persisted
/// locally via SettingsService (see Services/SettingsService.cs — a plain
/// JSON file in %AppData%\DAAMS\ControlCenter, not Firestore).
///
/// Display & Notification preferences save immediately with a 2-second "Saved"
/// indicator. System information is loaded live from LocalDatabaseService.
/// </summary>
public class SettingsViewModel : ViewModelBase, IDataSection
{
    private readonly SettingsService _settingsService;
    private readonly LocalDatabaseService? _dbService;
    private readonly DispatcherTimer? _autoRefreshTimer;
    private readonly Func<string>? _getFirestoreStatus;
    private DispatcherTimer? _savedIndicatorTimer;
    private bool _isInitializing;

    // ============================================================
    // 1. System Information (Live)
    // ============================================================
    public string ApplicationVersion => $"DAAMS Control Center {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev"}";

    public string DatabasePath => _dbService?.DatabasePath ?? LocalDatabaseService.ResolveDatabasePath();

    public string DatabaseStatusText => (_dbService != null ? _dbService.DatabaseFound : File.Exists(DatabasePath))
        ? "Found"
        : "Not found";

    private string _syncSummaryText = "Loading...";
    public string SyncSummaryText
    {
        get => _syncSummaryText;
        private set => SetProperty(ref _syncSummaryText, value);
    }

    private string _lastUploadAttemptText = "Loading...";
    public string LastUploadAttemptText
    {
        get => _lastUploadAttemptText;
        private set => SetProperty(ref _lastUploadAttemptText, value);
    }

    private string _lastEventReceivedText = "Loading...";
    public string LastEventReceivedText
    {
        get => _lastEventReceivedText;
        private set => SetProperty(ref _lastEventReceivedText, value);
    }

    public string FirestoreConnectionStatus => "Not connected. This app reads the local database.";
    public string MonitoringSystemConnectionStatus => "Not Connected";

    // ============================================================
    // 2. Display (Real, Saved immediately)
    // ============================================================
    private string? _savedStatusText;
    public string? SavedStatusText
    {
        get => _savedStatusText;
        private set => SetProperty(ref _savedStatusText, value);
    }

    public sealed record TimeRangeOption(TimeRange Range, string Label)
    {
        public override string ToString() => Label;
    }

    private TimeRange _defaultTimeRange = TimeRange.Today;
    public TimeRange DefaultTimeRange
    {
        get => _defaultTimeRange;
        set
        {
            if (SetProperty(ref _defaultTimeRange, value))
            {
                ViewContext.Current.Range = value;
                if (!_isInitializing)
                {
                    TriggerSavedIndicator();
                    SaveImmediate();
                }
            }
        }
    }

    public IReadOnlyList<TimeRangeOption> AvailableTimeRanges { get; } = new[]
    {
        new TimeRangeOption(TimeRange.Today, "Today"),
        new TimeRangeOption(TimeRange.Last7Days, "7 days"),
        new TimeRangeOption(TimeRange.Last30Days, "30 days"),
        new TimeRangeOption(TimeRange.All, "All")
    };

    private bool _autoRefreshEnabled = true;
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshEnabled, value))
            {
                ApplyAutoRefreshTimer();
                if (!_isInitializing)
                {
                    SaveImmediate();
                }
            }
        }
    }

    private string _autoRefreshIntervalInput = "30";
    public string AutoRefreshIntervalInput
    {
        get => _autoRefreshIntervalInput;
        set
        {
            if (SetProperty(ref _autoRefreshIntervalInput, value))
            {
                ApplyAutoRefreshTimer();
                if (!_isInitializing)
                {
                    SaveImmediate();
                }
            }
        }
    }

    private bool _highlightCriticalRows = true;
    public bool HighlightCriticalRows
    {
        get => _highlightCriticalRows;
        set
        {
            if (SetProperty(ref _highlightCriticalRows, value))
            {
                if (!_isInitializing)
                {
                    SaveImmediate();
                }
            }
        }
    }

    private bool _showRiskFactorBreakdown = true;
    public bool ShowRiskFactorBreakdown
    {
        get => _showRiskFactorBreakdown;
        set
        {
            if (SetProperty(ref _showRiskFactorBreakdown, value))
            {
                if (!_isInitializing)
                {
                    SaveImmediate();
                }
            }
        }
    }

    // ============================================================
    // 3. Notifications (Real, Saved immediately)
    // ============================================================
    private bool _notifyOnNewAlert = true;
    public bool NotifyOnNewAlert
    {
        get => _notifyOnNewAlert;
        set
        {
            if (SetProperty(ref _notifyOnNewAlert, value))
            {
                if (!_isInitializing)
                {
                    SaveImmediate();
                }
            }
        }
    }

    private bool _notifyOnHighRisk = true;
    public bool NotifyOnHighRisk
    {
        get => _notifyOnHighRisk;
        set
        {
            if (SetProperty(ref _notifyOnHighRisk, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _notifyOnCriticalRisk = true;
    public bool NotifyOnCriticalRisk
    {
        get => _notifyOnCriticalRisk;
        set
        {
            if (SetProperty(ref _notifyOnCriticalRisk, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _emailNotificationsEnabled;
    public bool EmailNotificationsEnabled
    {
        get => _emailNotificationsEnabled;
        set
        {
            if (SetProperty(ref _emailNotificationsEnabled, value))
            {
                MarkDirty();
            }
        }
    }

    // ============================================================
    // Work Hours (Shared Admin Settings)
    // ============================================================
    private int _currentWorkStartHour = 7;
    private int _currentWorkEndHour = 19;

    public string CurrentWorkHoursText => $"Current: {_currentWorkStartHour:D2}:00 to {_currentWorkEndHour:D2}:00";

    private string _workStartHourInput = string.Empty;
    public string WorkStartHourInput
    {
        get => _workStartHourInput;
        set
        {
            if (SetProperty(ref _workStartHourInput, value))
            {
                WorkHoursValidationMessage = null;
            }
        }
    }

    private string _workEndHourInput = string.Empty;
    public string WorkEndHourInput
    {
        get => _workEndHourInput;
        set
        {
            if (SetProperty(ref _workEndHourInput, value))
            {
                WorkHoursValidationMessage = null;
            }
        }
    }

    private string? _workHoursValidationMessage;
    public string? WorkHoursValidationMessage
    {
        get => _workHoursValidationMessage;
        private set => SetProperty(ref _workHoursValidationMessage, value);
    }

    private string? _workHoursSavedStatusText;
    public string? WorkHoursSavedStatusText
    {
        get => _workHoursSavedStatusText;
        private set => SetProperty(ref _workHoursSavedStatusText, value);
    }

    private DispatcherTimer? _workHoursSavedIndicatorTimer;

    public ICommand SaveWorkHoursCommand { get; }

    public Func<bool>? ConfirmPinCallback { get; set; }

    // ============================================================
    // 4. Engine Defaults (Read-only reference)
    // ============================================================
    public string WorkingHoursText => EngineDefaults.WorkingHoursText;
    public string OffHoursPenalty => EngineDefaults.OffHoursText;
    public string NearEdgeOfHoursCaution => EngineDefaults.NearEdgeText;
    public string BurstThresholdReference => EngineDefaults.BurstText;
    public string ClassificationWeightsText => EngineDefaults.ClassificationText;
    public string ActionWeightsText => EngineDefaults.ActionText;
    public string RiskBandsText => EngineDefaults.RiskBandsText;

    // ============================================================
    // 5. Inactive / Legacy Configuration Properties
    // ============================================================
    private bool _monitoringEnabled;
    public bool MonitoringEnabled
    {
        get => _monitoringEnabled;
        set
        {
            if (SetProperty(ref _monitoringEnabled, value))
            {
                OnPropertyChanged(nameof(MonitoringStatusText));
                MarkDirty();
            }
        }
    }

    public string MonitoringStatusText => MonitoringEnabled
        ? "Active (configuration only — does not start/stop the Monitoring System)"
        : "Paused (configuration only — does not start/stop the Monitoring System)";

    private string _monitoringIntervalInput = "3";
    public string MonitoringIntervalInput
    {
        get => _monitoringIntervalInput;
        set
        {
            if (SetProperty(ref _monitoringIntervalInput, value))
            {
                MarkDirty();
            }
        }
    }

    private string _mediumThresholdInput = "25";
    public string MediumThresholdInput
    {
        get => _mediumThresholdInput;
        set
        {
            if (SetProperty(ref _mediumThresholdInput, value))
            {
                MarkDirty();
            }
        }
    }

    private string _highThresholdInput = "50";
    public string HighThresholdInput
    {
        get => _highThresholdInput;
        set
        {
            if (SetProperty(ref _highThresholdInput, value))
            {
                MarkDirty();
            }
        }
    }

    private string _criticalThresholdInput = "75";
    public string CriticalThresholdInput
    {
        get => _criticalThresholdInput;
        set
        {
            if (SetProperty(ref _criticalThresholdInput, value))
            {
                MarkDirty();
            }
        }
    }

    private string _burstThresholdInput = "3";
    public string BurstThresholdInput
    {
        get => _burstThresholdInput;
        set
        {
            if (SetProperty(ref _burstThresholdInput, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _offHoursMonitoringEnabled = true;
    public bool OffHoursMonitoringEnabled
    {
        get => _offHoursMonitoringEnabled;
        set
        {
            if (SetProperty(ref _offHoursMonitoringEnabled, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _showSampleDataBanner = true;
    public bool ShowSampleDataBanner
    {
        get => _showSampleDataBanner;
        set
        {
            if (SetProperty(ref _showSampleDataBanner, value))
            {
                MarkDirty();
            }
        }
    }

    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    private string? _validationMessage;
    public string? ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    private string? _successMessage;
    public string? SuccessMessage
    {
        get => _successMessage;
        private set => SetProperty(ref _successMessage, value);
    }

    private bool _isConfirmingReset;
    public bool IsConfirmingReset
    {
        get => _isConfirmingReset;
        private set
        {
            if (SetProperty(ref _isConfirmingReset, value))
            {
                OnPropertyChanged(nameof(IsNotConfirmingReset));
            }
        }
    }

    public bool IsNotConfirmingReset => !IsConfirmingReset;

    public ICommand SaveChangesCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand ConfirmResetCommand { get; }
    public ICommand CancelResetCommand { get; }
    public ICommand CancelChangesCommand { get; }

    // ============================================================
    // Constructors
    // ============================================================
    public SettingsViewModel() : this(new SettingsService(), null, null, null)
    {
    }

    public SettingsViewModel(
        LocalDatabaseService? dbService,
        DispatcherTimer? autoRefreshTimer = null,
        Func<string>? getFirestoreStatus = null)
        : this(new SettingsService(), dbService, autoRefreshTimer, getFirestoreStatus)
    {
    }

    public SettingsViewModel(
        SettingsService settingsService,
        LocalDatabaseService? dbService = null,
        DispatcherTimer? autoRefreshTimer = null,
        Func<string>? getFirestoreStatus = null)
    {
        _settingsService = settingsService;
        _dbService = dbService;
        _autoRefreshTimer = autoRefreshTimer;
        _getFirestoreStatus = getFirestoreStatus;

        SaveChangesCommand = new RelayCommand(_ => SaveChanges());
        ResetToDefaultsCommand = new RelayCommand(_ => IsConfirmingReset = true);
        ConfirmResetCommand = new RelayCommand(_ => ResetToDefaults());
        CancelResetCommand = new RelayCommand(_ => IsConfirmingReset = false);
        CancelChangesCommand = new RelayCommand(_ => CancelChanges());
        SaveWorkHoursCommand = new RelayCommand(_ => SaveWorkHours());

        var (initialStart, initialEnd) = SettingsService.LoadWorkHours(_dbService?.DatabasePath);
        _currentWorkStartHour = initialStart;
        _currentWorkEndHour = initialEnd;
        _workStartHourInput = string.Empty;
        _workEndHourInput = string.Empty;

        _isInitializing = true;
        try
        {
            LoadFrom(_settingsService.Load());
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyAutoRefreshTimer();
        _ = RefreshAsync();
    }

    // ============================================================
    // Methods
    // ============================================================
    public void EnsureLoaded()
    {
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_dbService != null && _dbService.DatabaseFound)
        {
            try
            {
                var (total, pending) = await _dbService.GetSyncSummaryAsync();
                SyncSummaryText = $"{total} records, {pending} waiting to upload";
            }
            catch
            {
                SyncSummaryText = "Error reading database";
            }

            try
            {
                string? lastAttempt = await _dbService.GetLastSyncAttemptAsync();
                LastUploadAttemptText = string.IsNullOrWhiteSpace(lastAttempt)
                    ? "No upload attempts recorded"
                    : $"Last attempt {lastAttempt} (success)";
            }
            catch
            {
                LastUploadAttemptText = "No upload attempts recorded";
            }

            try
            {
                var logs = await _dbService.GetActivityLogsAsync();
                if (logs.Count > 0)
                {
                    LastEventReceivedText = logs[0].Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                else
                {
                    LastEventReceivedText = "No activities recorded";
                }
            }
            catch
            {
                LastEventReceivedText = "None";
            }
        }
        else
        {
            SyncSummaryText = "Database not found";
            LastUploadAttemptText = "No upload attempts recorded";
            LastEventReceivedText = "None";
        }

        OnPropertyChanged(nameof(FirestoreConnectionStatus));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(DatabaseStatusText));
    }

    private void TriggerSavedIndicator()
    {
        SavedStatusText = "Saved";
        _savedIndicatorTimer?.Stop();
        _savedIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _savedIndicatorTimer.Tick += (_, _) =>
        {
            _savedIndicatorTimer.Stop();
            SavedStatusText = null;
        };
        _savedIndicatorTimer.Start();
    }

    public void ApplyAutoRefreshTimer()
    {
        if (_autoRefreshTimer == null) return;

        if (int.TryParse(AutoRefreshIntervalInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval))
        {
            if (interval == 0 || !AutoRefreshEnabled)
            {
                _autoRefreshTimer.Stop();
            }
            else if (interval >= 10 && interval <= 300)
            {
                _autoRefreshTimer.Interval = TimeSpan.FromSeconds(interval);
                if (!_autoRefreshTimer.IsEnabled)
                {
                    _autoRefreshTimer.Start();
                }
            }
        }
    }

    private void SaveImmediate()
    {
        if (TryValidate(out AppSettingsData data, out _))
        {
            if (_settingsService.Save(data))
            {
                HasUnsavedChanges = false;
                ValidationMessage = null;
                TriggerSavedIndicator();
            }
        }
    }

    private void MarkDirty()
    {
        HasUnsavedChanges = true;
        SuccessMessage = null;
    }

    private void LoadFrom(AppSettingsData data)
    {
        _monitoringEnabled = data.MonitoringEnabled;
        _defaultTimeRange = data.DefaultTimeRange;
        _monitoringIntervalInput = data.MonitoringIntervalSeconds;

        _mediumThresholdInput = data.MediumRiskThreshold;
        _highThresholdInput = data.HighRiskThreshold;
        _criticalThresholdInput = data.CriticalRiskThreshold;
        _burstThresholdInput = data.BurstThreshold;
        _offHoursMonitoringEnabled = data.OffHoursMonitoringEnabled;

        _notifyOnNewAlert = data.NotifyOnNewAlert;
        _notifyOnHighRisk = data.NotifyOnHighRisk;
        _notifyOnCriticalRisk = data.NotifyOnCriticalRisk;
        _emailNotificationsEnabled = data.EmailNotificationsEnabled;

        _startWithWindows = data.StartWithWindows;
        _autoRefreshEnabled = data.AutoRefreshEnabled;
        _autoRefreshIntervalInput = string.IsNullOrWhiteSpace(data.AutoRefreshIntervalSeconds) ? "30" : data.AutoRefreshIntervalSeconds;
        _showRiskFactorBreakdown = data.ShowRiskFactorBreakdown;
        _highlightCriticalRows = data.HighlightCriticalRows;
        _showSampleDataBanner = data.ShowSampleDataBanner;

        OnPropertyChanged(string.Empty);

        HasUnsavedChanges = false;
        ValidationMessage = null;
    }

    private void CancelChanges()
    {
        _isInitializing = true;
        try
        {
            LoadFrom(_settingsService.Load());
        }
        finally
        {
            _isInitializing = false;
        }
        SuccessMessage = "Changes discarded.";
        ApplyAutoRefreshTimer();
    }

    private void ResetToDefaults()
    {
        IsConfirmingReset = false;
        _isInitializing = true;
        try
        {
            LoadFrom(AppSettingsData.Defaults);
        }
        finally
        {
            _isInitializing = false;
        }

        HasUnsavedChanges = true;
        SuccessMessage = "Fields restored to defaults. Click Save Changes to apply.";
        ApplyAutoRefreshTimer();
    }

    private bool TryValidate(out AppSettingsData data, out string? error)
    {
        data = new AppSettingsData();
        error = null;

        if (!TryParsePositiveInt(MonitoringIntervalInput, out int monitoringInterval))
        {
            error = "Monitoring Interval must be a positive whole number of seconds.";
            return false;
        }

        if (!TryParsePositiveInt(MediumThresholdInput, out int medium))
        {
            error = "The Medium risk threshold must be a positive whole number.";
            return false;
        }

        if (!TryParsePositiveInt(HighThresholdInput, out int high))
        {
            error = "The High risk threshold must be a positive whole number.";
            return false;
        }

        if (!TryParsePositiveInt(CriticalThresholdInput, out int critical))
        {
            error = "The Critical risk threshold must be a positive whole number.";
            return false;
        }

        if (!(medium < high && high < critical))
        {
            error = "Risk thresholds must increase in order: Medium < High < Critical.";
            return false;
        }

        if (!TryParsePositiveInt(BurstThresholdInput, out int burst))
        {
            error = "The Activity Burst Threshold must be a positive whole number.";
            return false;
        }

        if (!int.TryParse(AutoRefreshIntervalInput?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int autoRefreshInterval) ||
            autoRefreshInterval < 0 ||
            (autoRefreshInterval > 0 && autoRefreshInterval < 10) ||
            autoRefreshInterval > 300)
        {
            error = "Auto-Refresh Interval must be 0 (off) or between 10 and 300 seconds.";
            return false;
        }

        data = new AppSettingsData
        {
            MonitoringEnabled = MonitoringEnabled,
            MonitoringIntervalSeconds = monitoringInterval.ToString(CultureInfo.InvariantCulture),

            MediumRiskThreshold = medium.ToString(CultureInfo.InvariantCulture),
            HighRiskThreshold = high.ToString(CultureInfo.InvariantCulture),
            CriticalRiskThreshold = critical.ToString(CultureInfo.InvariantCulture),
            BurstThreshold = burst.ToString(CultureInfo.InvariantCulture),
            OffHoursMonitoringEnabled = OffHoursMonitoringEnabled,

            NotifyOnNewAlert = NotifyOnNewAlert,
            NotifyOnHighRisk = NotifyOnHighRisk,
            NotifyOnCriticalRisk = NotifyOnCriticalRisk,
            EmailNotificationsEnabled = EmailNotificationsEnabled,

            StartWithWindows = StartWithWindows,
            AutoRefreshEnabled = autoRefreshInterval > 0 && AutoRefreshEnabled,
            AutoRefreshIntervalSeconds = autoRefreshInterval.ToString(CultureInfo.InvariantCulture),
            ShowRiskFactorBreakdown = ShowRiskFactorBreakdown,
            HighlightCriticalRows = HighlightCriticalRows,
            ShowSampleDataBanner = ShowSampleDataBanner,
            DefaultTimeRange = DefaultTimeRange,
        };

        return true;
    }

    private static bool TryParsePositiveInt(string input, out int value)
    {
        return int.TryParse(input?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private void SaveChanges()
    {
        IsConfirmingReset = false;

        if (!TryValidate(out AppSettingsData data, out string? error))
        {
            ValidationMessage = error;
            SuccessMessage = null;
            return;
        }

        bool saved = _settingsService.Save(data);
        if (saved)
        {
            ValidationMessage = null;
            SuccessMessage = "Settings saved.";
            HasUnsavedChanges = false;
            TriggerSavedIndicator();
        }
        else
        {
            ValidationMessage = "Unable to save settings. Check that the application has permission to write to your user data folder.";
        }
    }

    public string EmailAlertsSummaryText
    {
        get
        {
            var enabledStr = Environment.GetEnvironmentVariable("DAAMS_EMAIL_ENABLED")?.Trim();
            bool isEnabled = string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase);
            var recipient = Environment.GetEnvironmentVariable("DAAMS_NOTIFICATION_EMAIL")?.Trim() ?? string.Empty;

            if (!isEnabled)
            {
                return "Off";
            }

            if (string.IsNullOrEmpty(recipient))
            {
                return "On (recipient not configured)";
            }

            return $"On ({MaskEmail(recipient)})";
        }
    }

    private static string MaskEmail(string email)
    {
        int atIndex = email.IndexOf('@');
        if (atIndex <= 0) return email;
        string user = email.Substring(0, atIndex);
        string domain = email.Substring(atIndex + 1);
        string maskedUser = user.Length > 0 ? user[0] + "***" : "***";
        return $"{maskedUser}@{domain}";
    }

    private void TriggerWorkHoursSavedIndicator()
    {
        WorkHoursSavedStatusText = "Saved";
        _workHoursSavedIndicatorTimer?.Stop();
        _workHoursSavedIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _workHoursSavedIndicatorTimer.Tick += (_, _) =>
        {
            _workHoursSavedIndicatorTimer?.Stop();
            WorkHoursSavedStatusText = null;
        };
        _workHoursSavedIndicatorTimer.Start();
    }

    private bool ConfirmAdminPin()
    {
        if (ConfirmPinCallback != null)
        {
            return ConfirmPinCallback();
        }

        try
        {
            var db = _dbService ?? new LocalDatabaseService();
            var prompt = new Views.PinPromptWindow(db, "Enter admin PIN to update work hours.")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            return prompt.ShowDialog() == true;
        }
        catch
        {
            return false;
        }
    }

    public void SaveWorkHours()
    {
        WorkHoursSavedStatusText = null;
        if (!int.TryParse(WorkStartHourInput?.Trim(), out int start) || start < 0 || start > 24)
        {
            WorkHoursValidationMessage = "Start hour must be between 0 and 24.";
            return;
        }

        if (!int.TryParse(WorkEndHourInput?.Trim(), out int end) || end < 0 || end > 24)
        {
            WorkHoursValidationMessage = "End hour must be between 0 and 24.";
            return;
        }

        if (start == end)
        {
            WorkHoursValidationMessage = "End hour must differ from start hour.";
            return;
        }

        WorkHoursValidationMessage = null;

        if (!ConfirmAdminPin())
        {
            return;
        }

        bool success = SettingsService.SaveWorkHours(start, end, _dbService?.DatabasePath);
        if (success)
        {
            _currentWorkStartHour = start;
            _currentWorkEndHour = end;
            OnPropertyChanged(nameof(CurrentWorkHoursText));
            WorkStartHourInput = string.Empty;
            WorkEndHourInput = string.Empty;
            TriggerWorkHoursSavedIndicator();
        }
        else
        {
            WorkHoursValidationMessage = "Unable to save work hours. Check folder permissions.";
        }
    }
}
