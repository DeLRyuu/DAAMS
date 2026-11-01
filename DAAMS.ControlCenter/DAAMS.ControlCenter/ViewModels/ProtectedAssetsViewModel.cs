using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
/// </summary>
public class ProtectedAssetsViewModel : DataSectionViewModelBase
{
    private readonly LocalDatabaseService _db;
    private readonly FirestoreService? _firestore;
    private readonly Action<NavigationSection, string>? _navigate;
    private readonly ObservableCollection<ProtectedAsset> _allAssets = new();
    private List<ProtectedAsset> _registeredAssets = new();
    private readonly List<string> _folderHistory = new();

    private List<ActivityLog> _cachedLogs = new();
    private List<CaseItem> _cachedCases = new();

    /// <summary>Filtered/sorted view of _allAssets — the DataGrid binds to this, not the raw list.</summary>
    public ICollectionView Assets { get; }

    private string? _currentFolder;
    public string? CurrentFolder
    {
        get => _currentFolder;
        private set
        {
            if (SetProperty(ref _currentFolder, value))
            {
                OnPropertyChanged(nameof(CurrentBreadcrumb));
                OnPropertyChanged(nameof(IsAtRoot));
                OnPropertyChanged(nameof(CanGoUp));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CurrentBreadcrumb => CurrentFolder is null
        ? "Protected Assets"
        : $"Protected Assets  >  {CurrentFolder}";

    public bool IsAtRoot => CurrentFolder is null;
    public bool CanGoUp => CurrentFolder is not null;

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
    public bool IsSelectedAssetProtected =>
        SelectedAsset is { Status: ProtectionStatus.Protected, IsInherited: false };

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
    public ICommand OpenFolderCommand { get; }
    public ICommand GoUpCommand { get; }

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

            ProtectedAsset selectedAsset = SelectedAsset;
            if (selectedAsset.AssetType == AssetType.Folder)
            {
                int explicitFilesInside = _registeredAssets.Count(asset =>
                    asset.AssetType == AssetType.File
                    && IsWithin(asset.Path, selectedAsset.Path));
                if (explicitFilesInside > 0)
                {
                    var result = System.Windows.MessageBox.Show(
                        $"This folder contains {explicitFilesInside} explicitly protected file row(s). " +
                        "Removing folder protection may change how those files are monitored. Continue?",
                        "Remove folder protection",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (result != System.Windows.MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
            }

            string promptText = selectedAsset.AssetType == AssetType.Folder
                ? "The folder and its files are not touched. Activity history is kept. Enter admin PIN to remove protection."
                : "The file is not touched. Activity history is kept. Enter admin PIN to remove protection.";
            var prompt = new PinPromptWindow(_db, promptText)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            if (prompt.ShowDialog() != true)
            {
                return;
            }

            string path = selectedAsset.Path;
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
        OpenFolderCommand = RelayCommand.FromAsync(
            OpenFolderAsync,
            ex => ActionError = ex.Message,
            parameter => parameter is ProtectedAsset asset && asset.AssetType == AssetType.Folder);
        GoUpCommand = RelayCommand.FromAsync(_ => GoUpAsync(),
            ex => ActionError = ex.Message,
            _ => !IsAtRoot);

        ViewContext.Current.RangeChanged += () =>
        {
            foreach (var asset in _allAssets)
            {
                var matchingLogs = _cachedLogs.Where(l => MatchesAsset(l, asset));
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
                .Where(l => MatchesAsset(l, _selectedAsset))
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
        _registeredAssets = assets;
        await RefreshCurrentFolderAsync();
        return assets.Count;
    }

    protected override void LoadSampleFallbackCore()
    {
        List<ProtectedAsset> assets = MockDataProvider.GetProtectedAssets();
        _cachedLogs = new();
        _cachedCases = new();
        ComputeUsageContext(assets);
        _registeredAssets = assets;
        _folderHistory.Clear();
        CurrentFolder = null;
        Apply(assets);
    }

    private void ComputeUsageContext(IEnumerable<ProtectedAsset> assets)
    {
        foreach (var asset in assets)
        {
            var matchingLogs = _cachedLogs.Where(l => MatchesAsset(l, asset)).ToList();
            var latestLog = matchingLogs.OrderByDescending(l => l.Timestamp).FirstOrDefault();

            asset.LastActivity = latestLog?.Timestamp;
            asset.LastActivityText = latestLog != null ? latestLog.Timestamp.ToString("yyyy-MM-dd HH:mm") : "—";
            asset.EventsInRange = matchingLogs.Count(l => ViewContext.Current.IsInRange(l.Timestamp));

            var matchingCases = _cachedCases.Where(c => MatchesCase(c, asset)).ToList();
            asset.OpenCases = matchingCases.Count(c => c.Status.IsOpen());

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

    private static bool MatchesAsset(ActivityLog log, ProtectedAsset asset)
    {
        if (MatchesAssetPath(log.ProtectedRoot, asset))
        {
            return true;
        }

        foreach (string? candidate in new[] { log.Path, log.AssetPath })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string[] paths = candidate.Split(" -> ", StringSplitOptions.TrimEntries);
            if (paths.Any(path => MatchesAssetPath(path, asset)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAssetPath(string? path, ProtectedAsset asset)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (asset.AssetType == AssetType.Folder)
        {
            string normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
            string folderPath = asset.Path.Replace('/', '\\').TrimEnd('\\');
            string folderPrefix = folderPath + "\\";
            return normalizedPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(normalizedPath, folderPath,
                       StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            path.Replace('/', '\\').TrimEnd('\\'),
            asset.Path.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesCase(CaseItem c, ProtectedAsset asset)
    {
        if (!string.IsNullOrEmpty(c.Path) && MatchesAssetPath(c.Path, asset))
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

        ApplyVisibleAssets(GetRootAssets(assets), selectedPath);
    }

    private static IEnumerable<ProtectedAsset> GetRootAssets(List<ProtectedAsset> assets) =>
        assets.Where(asset => !assets.Any(folder => folder != asset
            && folder.AssetType == AssetType.Folder
            && folder.Status == ProtectionStatus.Protected
            && IsWithin(asset.Path, folder.Path)));

    private async Task OpenFolderAsync(object? parameter)
    {
        if (parameter is not ProtectedAsset folder || folder.AssetType != AssetType.Folder)
        {
            return;
        }

        string[] entries = await Task.Run(() => Directory.GetFileSystemEntries(folder.Path));
        List<ProtectedAsset> children = CreateFolderRows(folder.Path, entries);
        _folderHistory.Add(folder.Path);
        CurrentFolder = folder.Path;
        ActionError = null;
        ApplyVisibleAssets(children, null);
    }

    private async Task GoUpAsync()
    {
        if (_folderHistory.Count == 0)
        {
            return;
        }

        string folderToRestore = _folderHistory[^1];
        int targetHistoryCount = _folderHistory.Count - 1;
        string? targetFolder = targetHistoryCount == 0
            ? null
            : _folderHistory[targetHistoryCount - 1];
        List<ProtectedAsset> targetAssets;
        if (targetFolder is null)
        {
            targetAssets = GetRootAssets(_registeredAssets).ToList();
        }
        else
        {
            string[] entries = await Task.Run(() => Directory.GetFileSystemEntries(targetFolder));
            targetAssets = CreateFolderRows(targetFolder, entries);
        }

        _folderHistory.RemoveAt(_folderHistory.Count - 1);
        CurrentFolder = targetFolder;
        ApplyVisibleAssets(targetAssets, folderToRestore);
        ActionError = null;
    }

    private async Task RefreshCurrentFolderAsync(string? selectedPath = null)
    {
        if (CurrentFolder is null)
        {
            ApplyVisibleAssets(GetRootAssets(_registeredAssets),
                selectedPath ?? SelectedAsset?.Path);
            return;
        }

        string folderPath = CurrentFolder;
        string[] entries = await Task.Run(() => Directory.GetFileSystemEntries(folderPath));
        ApplyVisibleAssets(CreateFolderRows(folderPath, entries), selectedPath ?? SelectedAsset?.Path);
    }

    private List<ProtectedAsset> CreateFolderRows(string folderPath, IEnumerable<string> entries)
    {
        ProtectedAsset? coveringFolder = FindCoveringProtectedFolder(folderPath);
        ProtectedAsset? parentAsset = _registeredAssets.FirstOrDefault(asset =>
            PathsEqual(asset.Path, folderPath));
        ProtectedAsset? inheritedFrom = coveringFolder ?? parentAsset;
        var children = new List<ProtectedAsset>();

        foreach (string entry in entries)
        {
            ProtectedAsset? registered = _registeredAssets.FirstOrDefault(asset =>
                PathsEqual(asset.Path, entry));
            if (registered != null)
            {
                children.Add(registered);
                continue;
            }

            bool isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
            bool isInherited = coveringFolder != null;
            string normalizedEntry = entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            children.Add(new ProtectedAsset
            {
                Path = entry,
                AssetName = Path.GetFileName(normalizedEntry) ?? normalizedEntry,
                AssetType = isDirectory ? AssetType.Folder : AssetType.File,
                Classification = inheritedFrom?.Classification ?? Classification.Public,
                ProtectedAt = inheritedFrom?.ProtectedAt ?? DateTime.MinValue,
                ProtectedBy = inheritedFrom?.ProtectedBy ?? string.Empty,
                Status = isInherited ? ProtectionStatus.Protected : ProtectionStatus.Unprotected,
                IsInherited = isInherited,
                InheritedFrom = isInherited ? coveringFolder?.Path : null
            });
        }

        ComputeUsageContext(children);
        return children;
    }

    private ProtectedAsset? FindCoveringProtectedFolder(string path)
    {
        return _registeredAssets
            .Where(asset => asset.AssetType == AssetType.Folder
                && asset.Status == ProtectionStatus.Protected
                && IsWithin(path, asset.Path))
            .OrderByDescending(asset => asset.Path.Length)
            .FirstOrDefault();
    }

    private void ApplyVisibleAssets(IEnumerable<ProtectedAsset> assets, string? selectedPath)
    {
        _allAssets.Clear();
        foreach (ProtectedAsset asset in assets)
        {
            _allAssets.Add(asset);
        }

        TotalCount = _allAssets.Count;
        ProtectedCount = _allAssets.Count(asset => asset.Status == ProtectionStatus.Protected);
        SelectedAsset = selectedPath == null
            ? null
            : _allAssets.FirstOrDefault(asset => PathsEqual(asset.Path, selectedPath));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string child, string parent)
    {
        string normalizedChild = NormalizePath(child);
        string normalizedParent = NormalizePath(parent).TrimEnd('\\');
        return string.Equals(normalizedChild, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedChild.StartsWith(normalizedParent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('/', '\\').TrimEnd('\\');

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
