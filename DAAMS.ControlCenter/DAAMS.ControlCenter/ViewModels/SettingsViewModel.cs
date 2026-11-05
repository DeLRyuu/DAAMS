using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly Func<Task>? _refreshFirestoreConnection;
    private readonly FirestoreService? _firestoreService;
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

    public string CloudStatusText => _getFirestoreStatus?.Invoke() ?? "Cloud: unreachable";
    public string FirestoreConnectionStatus => CloudStatusText;
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

    // ============================================================
    // Email Alerts (Shared Admin Settings)
    // ============================================================
    private string _notificationEmailInput = string.Empty;
    public string NotificationEmailInput
    {
        get => _notificationEmailInput;
        set
        {
            if (SetProperty(ref _notificationEmailInput, value))
            {
                EmailValidationMessage = null;
            }
        }
    }

    private string _savedNotificationEmail = string.Empty;

    public string EmailAlertsStatusLine
    {
        get
        {
            var enabledStr = Environment.GetEnvironmentVariable("DAAMS_EMAIL_ENABLED")?.Trim();
            bool isEnabled = string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase);
            var recipient = _savedNotificationEmail;

            string status = isEnabled ? "On" : "Off";
            if (!string.IsNullOrEmpty(recipient))
            {
                return $"Email alerts: {status} ({MaskEmail(recipient)})";
            }
            return $"Email alerts: {status}";
        }
    }

    private string? _emailValidationMessage;
    public string? EmailValidationMessage
    {
        get => _emailValidationMessage;
        private set => SetProperty(ref _emailValidationMessage, value);
    }

    private string? _emailSavedStatusText;
    public string? EmailSavedStatusText
    {
        get => _emailSavedStatusText;
        private set => SetProperty(ref _emailSavedStatusText, value);
    }

    private DispatcherTimer? _emailSavedIndicatorTimer;

    public ICommand SaveNotificationEmailCommand { get; }

    public Func<bool>? ConfirmPinCallback { get; set; }

    // ============================================================
    // SMTP Configuration (Encrypted DPAPI)
    // ============================================================
    private string _smtpSenderEmailInput = string.Empty;
    public string SmtpSenderEmailInput
    {
        get => _smtpSenderEmailInput;
        set
        {
            if (SetProperty(ref _smtpSenderEmailInput, value))
            {
                SmtpValidationMessage = null;
            }
        }
    }

    private bool _smtpEnabled;
    public bool SmtpEnabled
    {
        get => _smtpEnabled;
        set
        {
            if (SetProperty(ref _smtpEnabled, value))
            {
                SmtpValidationMessage = null;
                OnPropertyChanged(nameof(SmtpStatusLine));
            }
        }
    }

    private string _savedSmtpSenderEmail = string.Empty;
    private string _savedSmtpPasswordProtected = string.Empty;
    private bool _savedSmtpEnabled;

    public string SmtpStatusLine
    {
        get
        {
            string state = _savedSmtpEnabled ? "Enabled" : "Disabled";
            if (!string.IsNullOrWhiteSpace(_savedSmtpSenderEmail))
            {
                return $"SMTP: {state} ({_savedSmtpSenderEmail})";
            }
            return $"SMTP: {state}";
        }
    }

    private string? _smtpValidationMessage;
    public string? SmtpValidationMessage
    {
        get => _smtpValidationMessage;
        private set => SetProperty(ref _smtpValidationMessage, value);
    }

    private string? _smtpSavedStatusText;
    public string? SmtpSavedStatusText
    {
        get => _smtpSavedStatusText;
        private set => SetProperty(ref _smtpSavedStatusText, value);
    }

    private DispatcherTimer? _smtpSavedIndicatorTimer;

    public ICommand SaveSmtpSettingsCommand { get; }

    // ============================================================
    // Data Maintenance
    // ============================================================
    private bool _isMaintenanceBusy;
    public bool IsMaintenanceBusy
    {
        get => _isMaintenanceBusy;
        set
        {
            if (SetProperty(ref _isMaintenanceBusy, value))
            {
                OnPropertyChanged(nameof(CanRunMaintenance));
                OnPropertyChanged(nameof(CanRunLocalMaintenance));
            }
        }
    }

    public bool CanRunMaintenance => !IsMaintenanceBusy;
    public bool CanRunLocalMaintenance => CanRunMaintenance && DataMaintenanceService.CanCleanLocalData();
    public bool RequiresEngineForLocalCleanup => !DataMaintenanceService.CanCleanLocalData();

    private string? _maintenanceStatusText;
    public string? MaintenanceStatusText
    {
        get => _maintenanceStatusText;
        set => SetProperty(ref _maintenanceStatusText, value);
    }

    private string? _maintenanceMessage;
    public string? MaintenanceMessage
    {
        get => _maintenanceMessage;
        set => SetProperty(ref _maintenanceMessage, value);
    }

    private Brush _maintenanceMessageBrush = Brushes.Gray;
    public Brush MaintenanceMessageBrush
    {
        get => _maintenanceMessageBrush;
        set => SetProperty(ref _maintenanceMessageBrush, value);
    }

    public ICommand CleanLocalDataCommand { get; }
    public ICommand CleanCloudDataCommand { get; }
    public ICommand RefreshCloudConnectionCommand { get; }

    private bool _cleanLocalAll;
    public bool CleanLocalAll
    {
        get => _cleanLocalAll;
        set => SetProperty(ref _cleanLocalAll, value);
    }

    private bool _cleanCloudAll;
    public bool CleanCloudAll
    {
        get => _cleanCloudAll;
        set => SetProperty(ref _cleanCloudAll, value);
    }

    public event Func<Task>? DataResetCompleted;

    // ============================================================
    // Admin PIN Management (Phase 9f)
    // ============================================================
    private string? _pinValidationMessage;
    public string? PinValidationMessage
    {
        get => _pinValidationMessage;
        private set => SetProperty(ref _pinValidationMessage, value);
    }

    private string? _pinSuccessMessage;
    public string? PinSuccessMessage
    {
        get => _pinSuccessMessage;
        private set => SetProperty(ref _pinSuccessMessage, value);
    }

    private DispatcherTimer? _pinSuccessIndicatorTimer;

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
        Func<string>? getFirestoreStatus = null,
        Func<Task>? refreshFirestoreConnection = null,
        FirestoreService? firestoreService = null)
        : this(new SettingsService(), dbService, autoRefreshTimer, getFirestoreStatus, refreshFirestoreConnection, firestoreService)
    {
    }

    public SettingsViewModel(
        SettingsService settingsService,
        LocalDatabaseService? dbService = null,
        DispatcherTimer? autoRefreshTimer = null,
        Func<string>? getFirestoreStatus = null,
        Func<Task>? refreshFirestoreConnection = null,
        FirestoreService? firestoreService = null)
    {
        _settingsService = settingsService;
        _dbService = dbService;
        _autoRefreshTimer = autoRefreshTimer;
        _getFirestoreStatus = getFirestoreStatus;
        _refreshFirestoreConnection = refreshFirestoreConnection;
        _firestoreService = firestoreService;

        SaveChangesCommand = new RelayCommand(_ => SaveChanges());
        ResetToDefaultsCommand = new RelayCommand(_ => IsConfirmingReset = true);
        ConfirmResetCommand = new RelayCommand(_ => ResetToDefaults());
        CancelResetCommand = new RelayCommand(_ => IsConfirmingReset = false);
        CancelChangesCommand = new RelayCommand(_ => CancelChanges());
        SaveWorkHoursCommand = new RelayCommand(_ => SaveWorkHours());
        SaveNotificationEmailCommand = new RelayCommand(_ => SaveNotificationEmail());
        _savedNotificationEmail = SettingsService.LoadNotificationEmail(_dbService?.DatabasePath);
        _notificationEmailInput = _savedNotificationEmail;

        SaveSmtpSettingsCommand = new RelayCommand(param => SaveSmtpSettings(param as System.Windows.Controls.PasswordBox));
        CleanLocalDataCommand = RelayCommand.FromAsync(_ => CleanLocalDataAsync(), canExecute: _ => CanRunLocalMaintenance);
        CleanCloudDataCommand = RelayCommand.FromAsync(_ => CleanCloudDataAsync(), canExecute: _ => CanRunMaintenance);
        RefreshCloudConnectionCommand = RelayCommand.FromAsync(_ => RefreshCloudConnectionAsync());
        var (smtpSender, smtpPwProt, smtpEnabled) = SettingsService.LoadSmtpSettings(_dbService?.DatabasePath);
        _savedSmtpSenderEmail = smtpSender;
        _savedSmtpPasswordProtected = smtpPwProt;
        _savedSmtpEnabled = smtpEnabled;
        _smtpSenderEmailInput = smtpSender;
        _smtpEnabled = smtpEnabled;

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
        await RefreshCloudConnectionAsync();

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
                    : $"Last attempt {lastAttempt}";
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
        OnPropertyChanged(nameof(CloudStatusText));
        OnPropertyChanged(nameof(DatabasePath));
        OnPropertyChanged(nameof(DatabaseStatusText));
    }

    private async Task RefreshCloudConnectionAsync()
    {
        if (_refreshFirestoreConnection != null)
        {
            await _refreshFirestoreConnection();
        }
        OnPropertyChanged(nameof(CloudStatusText));
        OnPropertyChanged(nameof(FirestoreConnectionStatus));
    }

    public void NotifyCloudStatusChanged()
    {
        OnPropertyChanged(nameof(CloudStatusText));
        OnPropertyChanged(nameof(FirestoreConnectionStatus));
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
            var recipient = _savedNotificationEmail;

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

    private bool ConfirmAdminPin(string description = "Enter admin PIN to proceed.")
    {
        if (ConfirmPinCallback != null)
        {
            return ConfirmPinCallback();
        }

        try
        {
            var db = _dbService ?? new LocalDatabaseService();
            var prompt = new Views.PinPromptWindow(db, description)
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

    private void TriggerEmailSavedIndicator()
    {
        EmailSavedStatusText = "Saved";
        _emailSavedIndicatorTimer?.Stop();
        _emailSavedIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _emailSavedIndicatorTimer.Tick += (_, _) =>
        {
            _emailSavedIndicatorTimer?.Stop();
            EmailSavedStatusText = null;
        };
        _emailSavedIndicatorTimer.Start();
    }

    public void SaveNotificationEmail()
    {
        EmailSavedStatusText = null;
        string email = NotificationEmailInput?.Trim() ?? string.Empty;

        if (!SettingsService.IsValidEmail(email))
        {
            EmailValidationMessage = "Enter a valid email address (e.g. name@domain.com, max 254 characters).";
            return;
        }

        EmailValidationMessage = null;

        if (!ConfirmAdminPin())
        {
            return;
        }

        bool success = SettingsService.SaveNotificationEmail(email, _dbService?.DatabasePath);
        if (success)
        {
            _savedNotificationEmail = email;
            NotificationEmailInput = email;
            OnPropertyChanged(nameof(EmailAlertsStatusLine));
            OnPropertyChanged(nameof(EmailAlertsSummaryText));
            TriggerEmailSavedIndicator();
        }
        else
        {
            EmailValidationMessage = "Unable to save email settings. Check folder permissions.";
        }
    }

    public void SyncSmtpPasswordBox(System.Windows.Controls.PasswordBox? box)
    {
        if (box != null && !string.IsNullOrEmpty(_savedSmtpPasswordProtected))
        {
            string? decrypted = SettingsService.DecryptPassword(_savedSmtpPasswordProtected);
            if (!string.IsNullOrEmpty(decrypted))
            {
                box.Password = decrypted;
            }
        }
    }

    private void TriggerSmtpSavedIndicator()
    {
        SmtpSavedStatusText = "Saved";
        _smtpSavedIndicatorTimer?.Stop();
        _smtpSavedIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _smtpSavedIndicatorTimer.Tick += (_, _) =>
        {
            _smtpSavedIndicatorTimer?.Stop();
            SmtpSavedStatusText = null;
        };
        _smtpSavedIndicatorTimer.Start();
    }

    public void SaveSmtpSettings(System.Windows.Controls.PasswordBox? passwordBox)
    {
        SmtpSavedStatusText = null;
        string sender = SmtpSenderEmailInput?.Trim() ?? string.Empty;
        string newPassword = passwordBox?.Password ?? string.Empty;

        if (SmtpEnabled)
        {
            if (string.IsNullOrWhiteSpace(sender) || !SettingsService.IsValidEmail(sender))
            {
                SmtpValidationMessage = "Enter a valid sender email address (e.g. name@domain.com).";
                return;
            }

            if (string.IsNullOrEmpty(newPassword) && string.IsNullOrEmpty(_savedSmtpPasswordProtected))
            {
                SmtpValidationMessage = "App password is required when SMTP is enabled.";
                return;
            }
        }
        else if (!string.IsNullOrEmpty(sender) && !SettingsService.IsValidEmail(sender))
        {
            SmtpValidationMessage = "Enter a valid sender email address (e.g. name@domain.com).";
            return;
        }

        SmtpValidationMessage = null;

        if (!ConfirmAdminPin("Enter admin PIN to update SMTP settings."))
        {
            return;
        }

        string protectedBytes = _savedSmtpPasswordProtected;
        if (!string.IsNullOrEmpty(newPassword))
        {
            protectedBytes = SettingsService.EncryptPassword(newPassword);
        }

        bool success = SettingsService.SaveSmtpSettings(sender, protectedBytes, SmtpEnabled, _dbService?.DatabasePath);
        if (success)
        {
            _savedSmtpSenderEmail = sender;
            _savedSmtpPasswordProtected = protectedBytes;
            _savedSmtpEnabled = SmtpEnabled;
            OnPropertyChanged(nameof(SmtpStatusLine));
            TriggerSmtpSavedIndicator();
        }
        else
        {
            SmtpValidationMessage = "Unable to save SMTP settings. Check folder permissions.";
        }
    }

    private void TriggerPinSuccessIndicator(string message)
    {
        PinSuccessMessage = message;
        _pinSuccessIndicatorTimer?.Stop();
        _pinSuccessIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pinSuccessIndicatorTimer.Tick += (_, _) =>
        {
            _pinSuccessIndicatorTimer?.Stop();
            PinSuccessMessage = null;
        };
        _pinSuccessIndicatorTimer.Start();
    }

    public async Task<bool> ChangePinAsync(string currentPin, string newPin, string confirmPin)
    {
        PinValidationMessage = null;
        PinSuccessMessage = null;

        if (string.IsNullOrEmpty(currentPin))
        {
            PinValidationMessage = "Enter your current PIN.";
            return false;
        }

        if (string.IsNullOrEmpty(newPin))
        {
            PinValidationMessage = "Enter a new PIN.";
            return false;
        }

        var (success, message) = await Task.Run(() =>
            SettingsService.SaveNewPin(currentPin, newPin, confirmPin, _dbService?.DatabasePath));

        if (success)
        {
            PinValidationMessage = null;
            TriggerPinSuccessIndicator("PIN changed successfully.");
            return true;
        }
        else
        {
            PinValidationMessage = message;
            return false;
        }
    }
    // ============================================================
    // Data Maintenance Execution (Local & Cloud)
    // ============================================================
    private async Task CleanLocalDataAsync()
    {
        if (IsMaintenanceBusy) return;

        bool isAll = CleanLocalAll;

        // 3.a Existing PIN prompt, unchanged
        if (!ConfirmAdminPin(isAll ? "Enter admin PIN to clean local data (--all)." : "Enter admin PIN to clean local test data."))
            return;

        // 3.b If engine (main.py) is running, block with "Stop monitoring first." Do not kill it.
        if (DataMaintenanceService.IsEngineRunning())
        {
            MessageBox.Show("Stop monitoring first.", "DAAMS Control Center", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaintenanceMessage = "Stop monitoring first.";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
            return;
        }

        // 3.c & 3.e Confirmation dialog stating exactly what will be deleted
        var confirmDialog = new Views.CleanDataConfirmationWindow(isCloud: false, isAll: isAll)
        {
            Owner = Application.Current?.MainWindow
        };
        if (confirmDialog.ShowDialog() != true)
            return;

        // 4. Execution
        IsMaintenanceBusy = true;
        MaintenanceStatusText = "Working...";
        MaintenanceMessage = null;

        try
        {
            var result = await DataMaintenanceService.RunScriptAsync("reset_test_data.py", isAll ? "--all" : null, "RESET");

            if (result.ExitCode == 0)
            {
                string summary = string.IsNullOrWhiteSpace(result.Output) ? "Local data cleaned successfully." : result.Output.Trim();
                if (isAll)
                {
                    MaintenanceMessage = "Protected assets cleared and PIN reset to default.";
                }
                else
                {
                    MaintenanceMessage = "Local data cleanup completed.";
                }
                MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.StatusPositive") as Brush ?? Brushes.LightGreen;

                if (DataResetCompleted != null)
                {
                    await DataResetCompleted.Invoke();
                }

                var resultDialog = new Views.CleanDataResultWindow(
                    isAll ? "Clean Local Data (--all)" : "Clean Local Data",
                    isAll ? "Protected assets cleared and PIN reset to default." : "Completed successfully",
                    summary,
                    isSuccess: true)
                {
                    Owner = Application.Current?.MainWindow
                };
                resultDialog.ShowDialog();
            }
            else
            {
                string error = !string.IsNullOrWhiteSpace(result.Error) ? result.Error.Trim() : result.Output.Trim();
                MaintenanceMessage = "Local data cleanup failed.";
                MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;

                var resultDialog = new Views.CleanDataResultWindow(
                    isAll ? "Clean Local Data (--all)" : "Clean Local Data",
                    "Execution failed",
                    error,
                    isSuccess: false)
                {
                    Owner = Application.Current?.MainWindow
                };
                resultDialog.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            MaintenanceMessage = $"Error running cleanup: {ex.Message}";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
        }
        finally
        {
            CleanLocalAll = false;
            CleanCloudAll = false;
            IsMaintenanceBusy = false;
            MaintenanceStatusText = null;
        }
    }

    private async Task CleanCloudDataAsync()
    {
        if (IsMaintenanceBusy) return;

        bool isAll = CleanCloudAll;

        // 3.a Existing PIN prompt, unchanged
        if (!ConfirmAdminPin(isAll ? "Enter admin PIN to clean cloud data (--all)." : "Enter admin PIN to clean cloud test data."))
            return;

        // 3.b If engine (main.py) is running, block with "Stop monitoring first." Do not kill it.
        if (DataMaintenanceService.IsEngineRunning())
        {
            MessageBox.Show("Stop monitoring first.", "DAAMS Control Center", MessageBoxButton.OK, MessageBoxImage.Warning);
            MaintenanceMessage = "Stop monitoring first.";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
            return;
        }

        if (_firestoreService == null)
        {
            MaintenanceMessage = "Firestore service is unavailable.";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
            return;
        }

        IsMaintenanceBusy = true;
        MaintenanceStatusText = "Working...";
        MaintenanceMessage = null;

        string dryRunSummary;
        try
        {
            dryRunSummary = await _firestoreService.GetCloudDataSummaryAsync(isAll);
        }
        catch
        {
            IsMaintenanceBusy = false;
            MaintenanceStatusText = null;
            MaintenanceMessage = "Failed to connect to Firestore.";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
            var errorDialog = new Views.CleanDataResultWindow(
                "Clean Cloud Data",
                "Connection / Credential Check Failed",
                "Could not read the Firestore collection counts. Check the cloud connection status.",
                isSuccess: false)
            {
                Owner = Application.Current?.MainWindow
            };
            errorDialog.ShowDialog();
            return;
        }
        finally
        {
            IsMaintenanceBusy = false;
            MaintenanceStatusText = null;
        }

        // 3.c & 3.d Confirmation dialog showing counts; final button disabled until user types DELETE CLOUD DATA
        var confirmDialog = new Views.CleanDataConfirmationWindow(isCloud: true, isAll: isAll, dryRunOutput: dryRunSummary)
        {
            Owner = Application.Current?.MainWindow
        };
        if (confirmDialog.ShowDialog() != true)
            return;

        // 4. Execution
        IsMaintenanceBusy = true;
        MaintenanceStatusText = "Working...";
        MaintenanceMessage = null;

        try
        {
            string summary = await _firestoreService.DeleteCloudDataAsync(isAll);

            if (DataResetCompleted != null)
            {
                await DataResetCompleted.Invoke();
            }

            if (isAll)
            {
                MaintenanceMessage = "Protected assets cleared and PIN reset to default.";
            }
            else
            {
                MaintenanceMessage = "Cloud data cleanup completed.";
            }
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.StatusPositive") as Brush ?? Brushes.LightGreen;

            var resultDialog = new Views.CleanDataResultWindow(
                isAll ? "Clean Cloud Data (--all)" : "Clean Cloud Data",
                isAll ? "Protected assets cleared and PIN reset to default." : "Completed successfully",
                summary,
                isSuccess: true)
            {
                Owner = Application.Current?.MainWindow
            };
            resultDialog.ShowDialog();
        }
        catch
        {
            MaintenanceMessage = "Cloud data cleanup failed.";
            MaintenanceMessageBrush = Application.Current?.TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
            var resultDialog = new Views.CleanDataResultWindow(
                isAll ? "Clean Cloud Data (--all)" : "Clean Cloud Data",
                "Execution failed",
                "Firestore could not delete the requested documents. Check the cloud connection status.",
                isSuccess: false)
            {
                Owner = Application.Current?.MainWindow
            };
            resultDialog.ShowDialog();
        }
        finally
        {
            CleanLocalAll = false;
            CleanCloudAll = false;
            IsMaintenanceBusy = false;
            MaintenanceStatusText = null;
        }
    }
}
