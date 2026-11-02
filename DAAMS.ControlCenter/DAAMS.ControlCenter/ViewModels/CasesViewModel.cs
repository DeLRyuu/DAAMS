using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

public class CasesViewModel : DataSectionViewModelBase
{
    private const string EngineDefaultNote = "Pending investigation.";
    private static string CleanNote(string? n) =>
        string.Equals(n?.Trim(), EngineDefaultNote, StringComparison.OrdinalIgnoreCase) ? string.Empty : n ?? string.Empty;

    private readonly LocalDatabaseService _db;
    private readonly FirestoreService? _firestore;
    private readonly ObservableCollection<CaseItem> _allCases = new();

    public ICollectionView Cases { get; }

    public List<FilterOption<RiskLevel>> RiskOptions { get; } = FilterOption<RiskLevel>.AllOptions("All Risk Levels");

    private FilterOption<RiskLevel> _selectedRisk;
    public FilterOption<RiskLevel> SelectedRisk
    {
        get => _selectedRisk;
        set
        {
            if (SetProperty(ref _selectedRisk, value))
            {
                Cases.Refresh();
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
                Cases.Refresh();
                RecomputeCounts();
            }
        }
    }

    private CaseItem? _selectedCase;
    public CaseItem? SelectedCase
    {
        get => _selectedCase;
        set
        {
            if (SetProperty(ref _selectedCase, value))
            {
                NotesDraft = CleanNote(_selectedCase?.Notes);
                ResolutionDraft = _selectedCase?.Resolution ?? string.Empty;
                StatusError = null;
                OnPropertyChanged(nameof(HasSelectedClosedCase));
                OnPropertyChanged(nameof(SelectedGroup));
            }
        }
    }

    public bool HasSelectedClosedCase => _selectedCase != null && !_selectedCase.Status.IsOpen();
    public string? SelectedGroup => _selectedCase?.Group;

    private string _notesDraft = string.Empty;
    public string NotesDraft
    {
        get => _notesDraft;
        set => SetProperty(ref _notesDraft, value);
    }

    private string _resolutionDraft = string.Empty;
    public string ResolutionDraft
    {
        get => _resolutionDraft;
        set => SetProperty(ref _resolutionDraft, value);
    }

    private string? _statusError;
    public string? StatusError
    {
        get => _statusError;
        set => SetProperty(ref _statusError, value);
    }

    private int _needsReviewCount;
    public int NeedsReviewCount
    {
        get => _needsReviewCount;
        private set
        {
            if (SetProperty(ref _needsReviewCount, value))
            {
                OnPropertyChanged(nameof(HasNeedsReview));
                OnPropertyChanged(nameof(HasNothingNeedsReview));
            }
        }
    }
    public bool HasNeedsReview => NeedsReviewCount > 0;
    public bool HasNothingNeedsReview => NeedsReviewCount == 0 && OpenCount == 0;

    private int _openCount;
    public int OpenCount
    {
        get => _openCount;
        private set
        {
            if (SetProperty(ref _openCount, value))
            {
                OnPropertyChanged(nameof(HasNothingNeedsReview));
            }
        }
    }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public ICommand SetStatusCommand { get; }
    public ICommand SaveNotesCommand { get; }
    public ICommand CopySummaryCommand { get; }
    public ICommand ViewActivityCommand { get; }
    public ICommand ViewDeviceCommand { get; }

    public CasesViewModel(
        LocalDatabaseService db,
        Action<NavigationSection, string> navigateWith,
        FirestoreService? firestore = null)
    {
        _db = db;
        _firestore = firestore;

        Cases = CollectionViewSource.GetDefaultView(_allCases);
        Cases.Filter = FilterPredicate;
        Cases.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CaseItem.Group)));
        Cases.SortDescriptions.Add(new SortDescription(nameof(CaseItem.GroupOrder), ListSortDirection.Ascending));
        Cases.SortDescriptions.Add(new SortDescription(nameof(CaseItem.DetectedAt), ListSortDirection.Descending));

        _selectedRisk = RiskOptions[0];

        SetStatusCommand = RelayCommand.FromAsync(async param =>
        {
            if (param is CaseStatus status)
            {
                await SetStatusAsync(status);
            }
            else if (param is string str && Enum.TryParse<CaseStatus>(str, true, out var parsed))
            {
                await SetStatusAsync(parsed);
            }
        }, ex => ActionError = ex.Message);

        SaveNotesCommand = RelayCommand.FromAsync(async _ =>
        {
            if (SelectedCase == null) return;
            await _db.ApplyCaseChangeAsync(SelectedCase.AlertId, SelectedCase.Status, NotesDraft, ResolutionDraft);
            await RefreshAsync();
        }, ex => ActionError = ex.Message);

        CopySummaryCommand = new RelayCommand(_ =>
        {
            if (SelectedCase == null) return;
            var c = SelectedCase;
            string text = $"Alert ID: {c.AlertId}\n" +
                          $"Asset: {c.Asset}\n" +
                          $"Action: {c.Action}\n" +
                          $"User: {c.User}\n" +
                          $"Device: {c.Device}\n" +
                          $"Time: {c.DetectedAt:yyyy-MM-dd HH:mm:ss}\n" +
                          $"Risk: {c.RiskLevel}\n" +
                          $"Status: {c.Status}\n" +
                          $"Notes: {NotesDraft}\n" +
                          $"Assessment: Activity detected on protected asset requires investigation.";
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                ActionError = $"Clipboard failed: {ex.Message}";
            }
        });

        ViewActivityCommand = new RelayCommand(_ =>
        {
            if (SelectedCase is not null)
            {
                navigateWith(NavigationSection.ActivityLogs, SelectedCase.Asset);
            }
        });

        ViewDeviceCommand = new RelayCommand(_ =>
        {
            if (SelectedCase is not null)
            {
                navigateWith(NavigationSection.Devices, SelectedCase.Device);
            }
        });

        ViewContext.Current.RangeChanged += () =>
        {
            Cases.Refresh();
            RecomputeCounts();
        };
    }

    private async Task SetStatusAsync(CaseStatus newStatus)
    {
        if (SelectedCase == null) return;

        if (newStatus is CaseStatus.Resolved or CaseStatus.Dismissed && string.IsNullOrWhiteSpace(CleanNote(NotesDraft)))
        {
            StatusError = "Add a note before closing this incident.";
            SelectedCase.RefreshStatusBinding();
            return;
        }

        StatusError = null;
        string alertId = SelectedCase.AlertId;
        await _db.ApplyCaseChangeAsync(alertId, newStatus, NotesDraft, ResolutionDraft);
        await RefreshAsync();
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<CaseItem> cases = await _db.GetCasesAsync();
        Apply(cases);
        return cases.Count;
    }

    private void Apply(List<CaseItem> cases)
    {
        string? savedAlertId = SelectedCase?.AlertId;

        _allCases.Clear();
        foreach (var c in cases)
        {
            _allCases.Add(c);
        }

        RecomputeCounts();

        SelectedCase = (savedAlertId != null
                            ? _allCases.FirstOrDefault(c => c.AlertId == savedAlertId)
                            : null)
                        ?? _allCases.FirstOrDefault(c => c.Status.IsOpen());
    }

    private void RecomputeCounts()
    {
        var filtered = _allCases.Where(FilterPredicate).ToList();
        TotalCount = filtered.Count;
        NeedsReviewCount = filtered.Count(c => c.Status == CaseStatus.New);
        OpenCount = filtered.Count(c => c.Status.IsOpen());
    }

    protected override void ApplyPendingSearch(string s)
    {
        SearchText = s;
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not CaseItem item)
        {
            return false;
        }

        bool inRange = ViewContext.Current.IsInRange(item.DetectedAt);
        if (!inRange)
        {
            return false;
        }

        if (SelectedRisk.Value is RiskLevel risk && item.RiskLevel != risk)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = StringMatch.Has(item.AlertId, term)
                           || StringMatch.Has(item.User, term)
                           || StringMatch.Has(item.Device, term)
                           || StringMatch.Has(item.Asset, term)
                           || StringMatch.Has(item.Path, term);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
