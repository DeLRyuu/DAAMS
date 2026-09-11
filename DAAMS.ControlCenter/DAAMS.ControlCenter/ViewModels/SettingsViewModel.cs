namespace ControlCenter.ViewModels;

/// <summary>
/// Settings: local UI preference toggles plus read-only reference/system-info
/// panels. Nothing here is wired to the Monitoring Engine or Firestore yet —
/// these are foundation controls per the Phase 2 scope. Risk thresholds and
/// working-hours values shown in "Monitoring Reference" are the Monitoring
/// System's own configured defaults (reference doc §13); they are displayed
/// for context only and are not editable from here, because risk configuration
/// belongs to the Monitoring/Risk Assessment Engine, not the Control Center.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    // --- Notification settings (local UI state only, not wired) -------------
    private bool _notifyOnNewAlert = true;
    public bool NotifyOnNewAlert
    {
        get => _notifyOnNewAlert;
        set => SetProperty(ref _notifyOnNewAlert, value);
    }

    private bool _notifyCriticalOnly;
    public bool NotifyCriticalOnly
    {
        get => _notifyCriticalOnly;
        set => SetProperty(ref _notifyCriticalOnly, value);
    }

    private bool _emailNotificationsEnabled;
    public bool EmailNotificationsEnabled
    {
        get => _emailNotificationsEnabled;
        set => SetProperty(ref _emailNotificationsEnabled, value);
    }

    // --- Risk display preferences (local UI state only, not wired) ----------
    private bool _showRiskFactorBreakdown = true;
    public bool ShowRiskFactorBreakdown
    {
        get => _showRiskFactorBreakdown;
        set => SetProperty(ref _showRiskFactorBreakdown, value);
    }

    private bool _highlightCriticalRows = true;
    public bool HighlightCriticalRows
    {
        get => _highlightCriticalRows;
        set => SetProperty(ref _highlightCriticalRows, value);
    }

    // --- Application preferences (local UI state only, not wired) -----------
    private bool _showSampleDataBanner = true;
    public bool ShowSampleDataBanner
    {
        get => _showSampleDataBanner;
        set => SetProperty(ref _showSampleDataBanner, value);
    }

    // --- Read-only system information ---------------------------------------
    public string ApplicationVersion => "DAAMS Control Center — Phase 2 (Foundation Build)";
    public string FirestoreConnectionStatus => "Not Connected";
    public string MonitoringSystemConnectionStatus => "Not Connected";

    // --- Read-only Monitoring System reference values ------------------------
    public string DefaultWorkingHours => "07:00 – 19:00";
    public string OffHoursPenalty => "+25";
    public string NearEdgeOfHoursCaution => "+8";
    public string BurstThreshold => "3 recent relevant actions (Copy / Move / Delete on Private or Confidential assets)";
}
