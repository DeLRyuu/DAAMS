namespace ControlCenter.Models;

/// <summary>
/// Everything the Settings page persists, in one place. Plain data only —
/// no behavior, no validation (that lives in SettingsViewModel, where the
/// user-facing error messages are). Numeric values are stored as strings
/// because that's what round-trips cleanly to/from the settings TextBoxes;
/// SettingsViewModel is responsible for parsing/validating them before a
/// Save is accepted.
///
/// Defaults below intentionally reuse the DAAMS Monitoring System's own
/// documented defaults where one already exists (reference doc §8/§13),
/// rather than inventing new numbers:
///   - MonitoringIntervalSeconds: 3  (REGISTRY_RELOAD_INTERVAL_SECONDS default)
///   - Risk thresholds 25 / 50 / 75  (the Low/Medium/High/Critical boundaries)
///   - BurstThreshold: 3             (BURST_THRESHOLD default)
/// Every other field has no existing project default, so a reasonable,
/// clearly-documented development default was chosen instead.
/// </summary>
public class AppSettingsData
{
    // --- Monitoring Configuration (display/config only — see SettingsViewModel) ---
    public bool MonitoringEnabled { get; set; } = true;
    public string MonitoringIntervalSeconds { get; set; } = "3";

    // --- Risk Assessment Configuration (display/config only — real thresholds live in the Monitoring System) ---
    public string MediumRiskThreshold { get; set; } = "25";
    public string HighRiskThreshold { get; set; } = "50";
    public string CriticalRiskThreshold { get; set; } = "75";
    public string BurstThreshold { get; set; } = "3";
    public bool OffHoursMonitoringEnabled { get; set; } = true;

    // --- Notification Settings ---
    public bool NotifyOnNewAlert { get; set; } = true;
    public bool NotifyOnHighRisk { get; set; } = true;
    public bool NotifyOnCriticalRisk { get; set; } = true;
    public bool EmailNotificationsEnabled { get; set; }

    // --- System Preferences ---
    public bool StartWithWindows { get; set; }
    public bool AutoRefreshEnabled { get; set; }
    public string AutoRefreshIntervalSeconds { get; set; } = "30";
    public bool ShowRiskFactorBreakdown { get; set; } = true;
    public bool HighlightCriticalRows { get; set; } = true;
    public bool ShowSampleDataBanner { get; set; } = true;

    /// <summary>The one canonical set of defaults — used both for a first run (no settings file yet) and for "Reset to Defaults".</summary>
    public static AppSettingsData Defaults => new();
}
