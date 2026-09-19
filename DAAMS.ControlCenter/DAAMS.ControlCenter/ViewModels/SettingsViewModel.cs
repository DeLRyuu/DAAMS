using System;
using System.Globalization;
using System.Windows.Input;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Settings: monitoring/risk/notification/system preferences, persisted
/// locally via SettingsService (see Services/SettingsService.cs — a plain
/// JSON file in %AppData%\DAAMS\ControlCenter, not Firestore).
///
/// IMPORTANT SCOPE NOTE (Phase 6): every toggle/threshold on this page is
/// configuration UI only. None of it currently changes how the Python
/// Monitoring Engine detects activity, scores risk, or sends notifications —
/// that engine remains untouched, per the Phase 6 boundary. Values are saved
/// so a later integration phase has somewhere real to read them from.
///
/// Numeric fields are edited as strings (e.g. MonitoringIntervalInput) and
/// only parsed/validated when Save Changes runs. An invalid value is
/// rejected with a message and the previously saved configuration is kept —
/// nothing bad is ever written to disk.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;

    // ============================================================
    // 1. Monitoring Configuration
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

    /// <summary>Purely a label reflecting the toggle above — this page does not actually start/stop the Monitoring System.</summary>
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

    // ============================================================
    // 2. Risk Assessment Configuration
    // ============================================================
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

    // ============================================================
    // 3. Notification Settings
    // ============================================================
    private bool _notifyOnNewAlert = true;
    public bool NotifyOnNewAlert
    {
        get => _notifyOnNewAlert;
        set
        {
            if (SetProperty(ref _notifyOnNewAlert, value))
            {
                MarkDirty();
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
    // 4. System Preferences
    // ============================================================
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

    private bool _autoRefreshEnabled;
    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshEnabled, value))
            {
                MarkDirty();
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
                MarkDirty();
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
                MarkDirty();
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

    // ============================================================
    // 5. Configuration Actions / state
    // ============================================================
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

    /// <summary>True after the first "Reset to Defaults" click, while awaiting confirmation.</summary>
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

    /// <summary>Convenience for XAML — the built-in BooleanToVisibilityConverter has no "invert" option, so the normal action-buttons panel binds to this instead of adding a custom converter for one spot.</summary>
    public bool IsNotConfirmingReset => !IsConfirmingReset;

    public ICommand SaveChangesCommand { get; }
    public ICommand ResetToDefaultsCommand { get; }
    public ICommand ConfirmResetCommand { get; }
    public ICommand CancelResetCommand { get; }
    public ICommand CancelChangesCommand { get; }

    // ============================================================
    // Read-only reference info (unchanged from earlier phases)
    // ============================================================
    public string ApplicationVersion => "DAAMS Control Center — Phase 6 (Settings & Configuration)";
    public string FirestoreConnectionStatus => "Not Connected";
    public string MonitoringSystemConnectionStatus => "Not Connected";

    public string DefaultWorkingHours => "07:00 – 19:00";
    public string OffHoursPenalty => "+25";
    public string NearEdgeOfHoursCaution => "+8";
    public string BurstThresholdReference => "3 recent relevant actions (Copy / Move / Delete on Private or Confidential assets)";

    public SettingsViewModel() : this(new SettingsService())
    {
    }

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;

        SaveChangesCommand = new RelayCommand(_ => SaveChanges());
        ResetToDefaultsCommand = new RelayCommand(_ => IsConfirmingReset = true);
        ConfirmResetCommand = new RelayCommand(_ => ResetToDefaults());
        CancelResetCommand = new RelayCommand(_ => IsConfirmingReset = false);
        CancelChangesCommand = new RelayCommand(_ => CancelChanges());

        LoadFrom(_settingsService.Load());
    }

    private void MarkDirty()
    {
        HasUnsavedChanges = true;
        SuccessMessage = null;
    }

    private void LoadFrom(AppSettingsData data)
    {
        _monitoringEnabled = data.MonitoringEnabled;
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
        _autoRefreshIntervalInput = data.AutoRefreshIntervalSeconds;
        _showRiskFactorBreakdown = data.ShowRiskFactorBreakdown;
        _highlightCriticalRows = data.HighlightCriticalRows;
        _showSampleDataBanner = data.ShowSampleDataBanner;

        // Raise change notifications for every bindable property in one pass
        // rather than going through each setter (which would each mark the
        // page dirty and clear messages — wrong for a fresh load/reset/cancel).
        OnPropertyChanged(string.Empty);

        HasUnsavedChanges = false;
        ValidationMessage = null;
    }

    private void CancelChanges()
    {
        LoadFrom(_settingsService.Load());
        SuccessMessage = "Changes discarded.";
    }

    private void ResetToDefaults()
    {
        IsConfirmingReset = false;
        LoadFrom(AppSettingsData.Defaults);

        // Defaults are always internally valid, but Reset should still go
        // through the same single Save path as every other change — the
        // user sees the defaults populated and clicks Save Changes to
        // actually commit them, exactly like any other edit.
        HasUnsavedChanges = true;
        SuccessMessage = "Fields restored to defaults. Click Save Changes to apply.";
    }

    /// <summary>
    /// Parses and validates every numeric field. Returns true only if all
    /// values are valid positive numbers AND Medium &lt; High &lt; Critical.
    /// Never partially applies — either everything is valid, or nothing is saved.
    /// </summary>
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

        if (!TryParsePositiveInt(AutoRefreshIntervalInput, out int autoRefreshInterval))
        {
            error = "The Auto-Refresh Interval must be a positive whole number of seconds.";
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
            AutoRefreshEnabled = AutoRefreshEnabled,
            AutoRefreshIntervalSeconds = autoRefreshInterval.ToString(CultureInfo.InvariantCulture),
            ShowRiskFactorBreakdown = ShowRiskFactorBreakdown,
            HighlightCriticalRows = HighlightCriticalRows,
            ShowSampleDataBanner = ShowSampleDataBanner,
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
            return; // Nothing is written — the last valid saved configuration remains in effect.
        }

        bool saved = _settingsService.Save(data);
        if (saved)
        {
            ValidationMessage = null;
            SuccessMessage = "Settings saved.";
            HasUnsavedChanges = false;
        }
        else
        {
            // SettingsService already logged the real exception — the user
            // only ever sees this friendly message, never a stack trace.
            ValidationMessage = "Unable to save settings. Check that the application has permission to write to your user data folder.";
        }
    }
}
