using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Activity Logs: detected-activity history for protected assets, read from
/// Firestore's activity_logs collection, as reported by the DAAMS Monitoring
/// System. This view only displays events — it never invents or simulates
/// them (reference doc §9/§19).
/// </summary>
public class ActivityLogsViewModel : DataSectionViewModelBase
{
    private readonly LocalDatabaseService _db;
    private readonly FirestoreService? _firestore;
    private readonly Action<NavigationSection, string> _navigateWith;
    private readonly ObservableCollection<ActivityLog> _allActivity = new();
    private readonly List<ActivityLog> _rawActivity = new();
    private List<CaseItem>? _cachedCases;

    public ICollectionView Activity { get; }

    public DataGridRowDetailsVisibilityMode RowDetailsVisibilityMode { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private int _rawEventCount;
    public int RawEventCount
    {
        get => _rawEventCount;
        private set => SetProperty(ref _rawEventCount, value);
    }

    public string CountSummary => $"{TotalCount} {(TotalCount == 1 ? "row" : "rows")} from {RawEventCount} {(RawEventCount == 1 ? "activity" : "activities")}";
    public string TotalCountText => $"{TotalCount} detected {(TotalCount == 1 ? "activity" : "activities")}";

    private bool _collapseRepeats = true;
    public bool CollapseRepeats
    {
        get => _collapseRepeats;
        set
        {
            if (SetProperty(ref _collapseRepeats, value))
            {
                ApplyCollapsedActivity();
                RecomputeCounts();
            }
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Activity.Refresh();
                RecomputeCounts();
            }
        }
    }

    // Quick chips
    private bool _filterLast24Hours;
    public bool FilterLast24Hours
    {
        get => _filterLast24Hours;
        set
        {
            if (SetProperty(ref _filterLast24Hours, value))
            {
                Activity.Refresh();
                RecomputeCounts();
            }
        }
    }

    private bool _filterOffHours;
    public bool FilterOffHours
    {
        get => _filterOffHours;
        set
        {
            if (SetProperty(ref _filterOffHours, value))
            {
                Activity.Refresh();
                RecomputeCounts();
            }
        }
    }

    private bool _filterHighAndAbove;
    public bool FilterHighAndAbove
    {
        get => _filterHighAndAbove;
        set
        {
            if (SetProperty(ref _filterHighAndAbove, value))
            {
                Activity.Refresh();
                RecomputeCounts();
            }
        }
    }

    public ObservableCollection<FilterOption<ActionType>> ActionOptions { get; } = new();

    private FilterOption<ActionType> _selectedAction;
    public FilterOption<ActionType> SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (SetProperty(ref _selectedAction, value))
            {
                Activity.Refresh();
                RecomputeCounts();
            }
        }
    }

    public List<FilterOption<Classification>> ClassificationOptions { get; } = FilterOption<Classification>.AllOptions("All Classifications");

    private FilterOption<Classification> _selectedClassification;
    public FilterOption<Classification> SelectedClassification
    {
        get => _selectedClassification;
        set
        {
            if (SetProperty(ref _selectedClassification, value))
            {
                Activity.Refresh();
                RecomputeCounts();
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
                RecomputeCounts();
            }
        }
    }

    /// <summary>Clears search text, dropdown filters, and quick chips back to defaults in one step.</summary>
    public ICommand ResetFiltersCommand { get; }

    public ICommand OpenCaseCommand { get; }
    public ICommand ViewDeviceCommand { get; }
    public ICommand ExportCsvCommand { get; }

    public ActivityLogsViewModel(LocalDatabaseService db, Action<NavigationSection, string> navigateWith, FirestoreService? firestore = null)
    {
        _db = db;
        _navigateWith = navigateWith;
        _firestore = firestore;

        foreach (var opt in FilterOption<ActionType>.AllOptions("All Actions"))
        {
            ActionOptions.Add(opt);
        }
        _selectedAction = ActionOptions[0];
        _selectedClassification = ClassificationOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];

        RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed;

        // Flat, time-ordered list (newest first). No day grouping: the Timestamp column already shows the date.
        Activity = CollectionViewSource.GetDefaultView(_allActivity);
        Activity.Filter = FilterPredicate;
        Activity.SortDescriptions.Add(new SortDescription(nameof(ActivityLog.Timestamp), ListSortDirection.Descending));

        ResetFiltersCommand = new RelayCommand(_ => ResetFilters());

        OpenCaseCommand = new RelayCommand(param =>
        {
            string? alertId = param as string;
            if (alertId == null && param is ActivityLog log)
            {
                alertId = log.MatchedAlertId;
            }

            if (!string.IsNullOrEmpty(alertId))
            {
                _navigateWith(NavigationSection.SecurityAlerts, alertId);
            }
        });

        ViewDeviceCommand = new RelayCommand(param =>
        {
            string? device = param as string;
            if (string.IsNullOrWhiteSpace(device) && param is ActivityLog log)
            {
                device = log.Device;
            }

            if (!string.IsNullOrWhiteSpace(device))
            {
                _navigateWith(NavigationSection.Devices, device);
            }
        });

        ExportCsvCommand = new RelayCommand(_ => ExportCsv());

        ViewContext.Current.RangeChanged += () =>
        {
            Activity.Refresh();
            RecomputeCounts();
        };
    }

    private void ResetFilters()
    {
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));

        if (ActionOptions.Count > 0)
        {
            _selectedAction = ActionOptions[0];
            OnPropertyChanged(nameof(SelectedAction));
        }

        _selectedClassification = ClassificationOptions[0];
        OnPropertyChanged(nameof(SelectedClassification));

        _selectedRiskLevel = RiskLevelOptions[0];
        OnPropertyChanged(nameof(SelectedRiskLevel));

        _filterLast24Hours = false;
        OnPropertyChanged(nameof(FilterLast24Hours));

        _filterOffHours = false;
        OnPropertyChanged(nameof(FilterOffHours));

        _filterHighAndAbove = false;
        OnPropertyChanged(nameof(FilterHighAndAbove));

        Activity.Refresh();
    }

    protected override void ApplyPendingSearch(string s)
    {
        SearchText = s;
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        try
        {
            _cachedCases = await _db.GetCasesAsync();
        }
        catch
        {
            _cachedCases = null;
        }

        List<ActivityLog> activity = await _db.GetActivityLogsAsync();
        Apply(activity);
        return activity.Count;
    }

    private void Apply(List<ActivityLog> activity)
    {
        _rawActivity.Clear();
        _rawActivity.AddRange(activity);

        MatchCases(_rawActivity, _cachedCases);

        ApplyCollapsedActivity();
        UpdateActionOptions(activity);
    }

    private void ApplyCollapsedActivity()
    {
        var collapsed = CollapseRepeats
            ? ActivityCollapser.Collapse(_rawActivity)
            : _rawActivity.ToList();

        _allActivity.Clear();
        foreach (ActivityLog entry in collapsed.OrderByDescending(a => a.Timestamp))
        {
            _allActivity.Add(entry);
        }

        RecomputeCounts();
        OnPropertyChanged(nameof(CountSummary));
    }

    private static void MatchCases(IEnumerable<ActivityLog> logs, List<CaseItem>? cases)
    {
        if (cases == null)
        {
            return;
        }

        foreach (var c in cases)
        {
            if (c == null)
            {
                continue;
            }

            var detectedAt = c.DetectedAt;
            var path = c.Path;
            var action = c.Action;
            var alertId = c.AlertId;

            if (string.IsNullOrEmpty(alertId))
            {
                continue;
            }

            foreach (var log in logs)
            {
                if (!string.IsNullOrEmpty(log.MatchedAlertId))
                {
                    continue;
                }

                if (detectedAt == log.Timestamp &&
                    string.Equals(path, log.Path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(action.ToString(), log.Action.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    log.MatchedAlertId = alertId;
                }
            }
        }
    }

    private void UpdateActionOptions(List<ActivityLog> activity)
    {
        var selectedValue = SelectedAction?.Value;
        var existingActions = activity.Select(a => a.Action).ToHashSet();

        var allOptions = FilterOption<ActionType>.AllOptions("All Actions");
        var matchingOptions = allOptions
            .Where(opt => opt.Value is not ActionType act || existingActions.Contains(act))
            .ToList();

        ActionOptions.Clear();
        foreach (var opt in matchingOptions)
        {
            ActionOptions.Add(opt);
        }

        SelectedAction = ActionOptions.FirstOrDefault(o => Equals(o.Value, selectedValue)) ?? ActionOptions[0];
    }

    private void RecomputeCounts()
    {
        var filtered = _allActivity.Where(FilterPredicate).ToList();
        TotalCount = filtered.Count;
        RawEventCount = filtered.Sum(a => a.HasRepeats ? a.MergedTimestamps.Count : 1);
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(TotalCountText));
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not ActivityLog entry)
        {
            return false;
        }

        if (!ViewContext.Current.IsInRange(entry.Timestamp))
        {
            return false;
        }

        if (FilterLast24Hours)
        {
            DateTime localTimestamp = entry.Timestamp.Kind == DateTimeKind.Utc ? entry.Timestamp.ToLocalTime() : entry.Timestamp;
            if (localTimestamp < DateTime.Now.AddHours(-24))
            {
                return false;
            }
        }

        if (FilterOffHours)
        {
            int hour = entry.Timestamp.Hour;
            if (hour >= EngineDefaults.WorkStartHour && hour < EngineDefaults.WorkEndHour)
            {
                return false;
            }
        }

        if (FilterHighAndAbove)
        {
            if (entry.RiskLevel is not (RiskLevel.High or RiskLevel.Critical))
            {
                return false;
            }
        }

        if (SelectedAction?.Value is ActionType action && entry.Action != action)
        {
            return false;
        }

        if (SelectedClassification?.Value is Classification classification && entry.Classification != classification)
        {
            return false;
        }

        if (SelectedRiskLevel?.Value is RiskLevel riskLevel && entry.RiskLevel != riskLevel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = StringMatch.Has(entry.User, term)
                           || StringMatch.Has(entry.Device, term)
                           || StringMatch.Has(entry.Asset, term)
                           || StringMatch.Has(entry.Path, term)
                           || StringMatch.Has(entry.Action.ToString(), term)
                           || StringMatch.Has(entry.Classification.ToString(), term)
                           || StringMatch.Has(entry.RiskLevel?.ToString(), term);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private void ExportCsv()
    {
        var rowsToExport = _rawActivity.Where(FilterPredicate).OrderByDescending(a => a.Timestamp).ToList();

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"daams-activity-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (sfd.ShowDialog() == true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,User,Device,Asset,Path,Action,Classification,RiskScore,RiskLevel,RiskFactors");

            foreach (var row in rowsToExport)
            {
                var factors = string.Join("; ", (row.RiskFactors ?? new List<RiskFactor>()).Select(rf => rf.Description ?? rf.ToString()));
                var line = string.Join(",",
                    EscapeCsv(row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                    EscapeCsv(row.User),
                    EscapeCsv(row.Device),
                    EscapeCsv(row.Asset),
                    EscapeCsv(row.Path),
                    EscapeCsv(row.Action.ToString()),
                    EscapeCsv(row.Classification.ToString()),
                    EscapeCsv(row.RiskScore?.ToString()),
                    EscapeCsv(row.RiskLevel?.ToString()),
                    EscapeCsv(factors)
                );
                sb.AppendLine(line);
            }

            try
            {
                File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ActionError = $"Export failed: {ex.Message}";
                return;
            }
        }
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
