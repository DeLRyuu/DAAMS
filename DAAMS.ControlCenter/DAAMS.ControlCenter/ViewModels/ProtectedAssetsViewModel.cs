using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ControlCenter.Data;
using ControlCenter.Models;
using ControlCenter.Services;
using ControlCenter.Views;

namespace ControlCenter.ViewModels;

/// <summary>
/// Protected Assets: browse/search/filter the assets currently registered with
/// DAAMS protection, enriched with usage context, activity metrics, and detail drawer.
/// Read-only presentation — actual protect/unprotect actions require the
/// Windows context menu + Monitoring System, which this app does not implement.
/// </summary>
public class ProtectedAssetsViewModel : DataSectionViewModelBase
{
    private readonly LocalDatabaseService _db;
    private readonly FirestoreService? _firestore;
    private readonly Action<NavigationSection, string>? _navigate;
    private readonly ObservableCollection<ProtectedAsset> _allAssets = new();

    private List<ActivityLog> _cachedLogs = new();
    private List<CaseItem> _cachedCases = new();

    /// <summary>Filtered/sorted view of _allAssets — the DataGrid binds to this, not the raw list.</summary>
    public ICollectionView Assets { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private int _protectedCount;
    public int ProtectedCount
    {
        get => _protectedCount;
        private set => SetProperty(ref _protectedCount, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Assets.Refresh();
            }
        }
    }

    public List<FilterOption<Classification>> ClassificationOptions { get; }
        = FilterOption<Classification>.AllOptions("All Classifications");

    private FilterOption<Classification> _selectedClassification;
    public FilterOption<Classification> SelectedClassification
    {
        get => _selectedClassification;
        set
        {
            if (SetProperty(ref _selectedClassification, value))
            {
                Assets.Refresh();
            }
        }
    }

    public List<FilterOption<ProtectionStatus>> StatusOptions { get; }
        = FilterOption<ProtectionStatus>.AllOptions("All Statuses");

    private FilterOption<ProtectionStatus> _selectedStatus;
    public FilterOption<ProtectionStatus> SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                Assets.Refresh();
            }
        }
    }

    private ProtectedAsset? _selectedAsset;
    public ProtectedAsset? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                if (_selectedAsset != null)
                {
                    DrawerSelectedClassification = _selectedAsset.Classification;
                }
                OnPropertyChanged(nameof(IsDrawerOpen));
                OnPropertyChanged(nameof(IsSelectedAssetProtected));
                UpdateRecentEvents();
            }
        }
    }

    public bool IsDrawerOpen => SelectedAsset != null;
    public bool IsSelectedAssetProtected => SelectedAsset?.Status == ProtectionStatus.Protected;

    public List<Classification> AvailableClassifications { get; } = new()
    {
        Classification.Public,
        Classification.Sensitive,
        Classification.Private,
        Classification.Confidential
    };

    private Classification _drawerSelectedClassification = Classification.Public;
    public Classification DrawerSelectedClassification
    {
        get => _drawerSelectedClassification;
        set => SetProperty(ref _drawerSelectedClassification, value);
    }

    public ObservableCollection<ActivityLog> RecentEvents { get; } = new();

    public ICommand CloseDrawerCommand { get; }
    public ICommand ViewActivityCommand { get; }
    public ICommand ViewAlertsCommand { get; }
    public ICommand ChangeClassificationCommand { get; }
    public ICommand RemoveProtectionCommand { get; }

    public ProtectedAssetsViewModel(LocalDatabaseService db, FirestoreService? firestore = null, Action<NavigationSection, string>? navigate = null)
    {
        _db = db;
        _firestore = firestore;
        _navigate = navigate;

        Assets = CollectionViewSource.GetDefaultView(_allAssets);
        Assets.Filter = FilterPredicate;

        _selectedClassification = ClassificationOptions[0];
        _selectedStatus = StatusOptions[0];

        CloseDrawerCommand = new RelayCommand(_ => SelectedAsset = null);
        ViewActivityCommand = new RelayCommand(_ =>
        {
            if (SelectedAsset != null)
            {
                NavigateTo(NavigationSection.ActivityLogs, SelectedAsset.Path);
            }
        });
        ViewAlertsCommand = new RelayCommand(_ =>
        {
            if (SelectedAsset != null)
            {
                NavigateTo(NavigationSection.SecurityAlerts, SelectedAsset.AssetName);
            }
        });
        ChangeClassificationCommand = RelayCommand.FromAsync(async _ =>
        {
            if (SelectedAsset == null)
            {
                return;
            }

            var prompt = new PinPromptWindow(_db, "Applies to future events only. Enter admin PIN to change classification.")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            if (prompt.ShowDialog() != true)
            {
                return;
            }

            string path = SelectedAsset.Path;
            string newClassification = DrawerSelectedClassification.ToString();

            bool success = await _db.UpdateAssetClassificationAsync(path, newClassification);
            if (success)
            {
                await RefreshAsync();
            }
            else
            {
                ActionError = "Failed to update asset classification.";
            }
        }, ex => ActionError = ex.Message);
        RemoveProtectionCommand = RelayCommand.FromAsync(async _ =>
        {
            if (SelectedAsset == null)
            {
                return;
            }

            var prompt = new PinPromptWindow(_db, "The file is not touched. Activity history is kept. Enter admin PIN to remove protection.")
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            if (prompt.ShowDialog() != true)
            {
                return;
            }

            string path = SelectedAsset.Path;
            bool success = await _db.RemoveAssetProtectionAsync(path);
            if (success)
            {
                await RefreshAsync();
            }
            else
            {
                ActionError = "Failed to remove asset protection.";
            }
        }, ex => ActionError = ex.Message);

        ViewContext.Current.RangeChanged += () =>
        {
            foreach (var asset in _allAssets)
            {
                var matchingLogs = _cachedLogs.Where(l => MatchesAsset(l.Path, asset));
                asset.EventsInRange = matchingLogs.Count(l => ViewContext.Current.IsInRange(l.Timestamp));
            }
        };
    }

    public ProtectedAssetsViewModel(FirestoreService firestore) : this(new LocalDatabaseService(), firestore)
    {
    }

    private void NavigateTo(NavigationSection section, string search)
    {
        if (_navigate != null)
        {
            _navigate(section, search);
            return;
        }

        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.NavigateWith(section, search);
            return;
        }

        ViewContext.Current.PendingSearch = search;
    }

    private void UpdateRecentEvents()
    {
        RecentEvents.Clear();
        if (_selectedAsset != null)
        {
            var top5 = _cachedLogs
                .Where(l => MatchesAsset(l.Path, _selectedAsset))
                .OrderByDescending(l => l.Timestamp)
                .Take(5);

            foreach (var item in top5)
            {
                RecentEvents.Add(item);
            }
        }
    }

    protected override void ApplyPendingSearch(string s)
    {
        SearchText = s;
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<ProtectedAsset> assets = await _db.GetProtectedAssetsAsync();
        try
        {
            _cachedLogs = await _db.GetActivityLogsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProtectedAssetsViewModel] Failed to load activity logs: {ex}");
            _cachedLogs = new();
        }

        try
        {
            _cachedCases = await _db.GetCasesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProtectedAssetsViewModel] Failed to load cases: {ex}");
            _cachedCases = new();
        }

        ComputeUsageContext(assets);
        Apply(assets);
        return assets.Count;
    }

    protected override void LoadSampleFallbackCore()
    {
        List<ProtectedAsset> assets = MockDataProvider.GetProtectedAssets();
        _cachedLogs = new();
        _cachedCases = new();
        ComputeUsageContext(assets);
        Apply(assets);
    }

    private void ComputeUsageContext(IEnumerable<ProtectedAsset> assets)
    {
        foreach (var asset in assets)
        {
            var matchingLogs = _cachedLogs.Where(l => MatchesAsset(l.Path, asset)).ToList();
            var latestLog = matchingLogs.OrderByDescending(l => l.Timestamp).FirstOrDefault();

            asset.LastActivity = latestLog?.Timestamp;
            asset.LastActivityText = latestLog != null ? latestLog.Timestamp.ToString("yyyy-MM-dd HH:mm") : "—";
            asset.EventsInRange = matchingLogs.Count(l => ViewContext.Current.IsInRange(l.Timestamp));

            var matchingCases = _cachedCases.Where(c => MatchesCase(c, asset)).ToList();
            asset.OpenCases = matchingCases.Count(c => c.Status is CaseStatus.New or CaseStatus.Investigating);

            RiskLevel? maxRisk = null;
            foreach (var l in matchingLogs)
            {
                if (maxRisk == null || l.RiskLevel > maxRisk.Value)
                {
                    maxRisk = l.RiskLevel;
                }
            }
            foreach (var c in matchingCases)
            {
                if (maxRisk == null || c.RiskLevel > maxRisk.Value)
                {
                    maxRisk = c.RiskLevel;
                }
            }
            asset.HighestRisk = maxRisk;
        }
    }

    private static bool MatchesAsset(string? path, ProtectedAsset asset)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (asset.AssetType == AssetType.Folder)
        {
            string folderPrefix = asset.Path.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path, asset.Path, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(path, asset.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesCase(CaseItem c, ProtectedAsset asset)
    {
        if (!string.IsNullOrEmpty(c.Path) && MatchesAsset(c.Path, asset))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(c.Asset))
        {
            if (string.Equals(c.Asset, asset.AssetName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Asset, asset.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (asset.AssetType == AssetType.Folder)
            {
                string folderPrefix = asset.Path.TrimEnd('\\', '/') + "\\";
                if (c.Asset.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Apply(List<ProtectedAsset> assets)
    {
        string? selectedPath = SelectedAsset?.Path;

        _allAssets.Clear();
        foreach (ProtectedAsset asset in assets)
        {
            _allAssets.Add(asset);
        }

        TotalCount = _allAssets.Count;
        ProtectedCount = _allAssets.Count(a => a.Status == ProtectionStatus.Protected);

        if (selectedPath != null)
        {
            SelectedAsset = _allAssets.FirstOrDefault(a => a.Path == selectedPath);
        }
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not ProtectedAsset asset)
        {
            return false;
        }

        if (SelectedClassification.Value is Classification classification && asset.Classification != classification)
        {
            return false;
        }

        if (SelectedStatus.Value is ProtectionStatus status && asset.Status != status)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = StringMatch.Has(asset.AssetName, term)
                           || StringMatch.Has(asset.Path, term);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
