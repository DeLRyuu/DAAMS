using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using ControlCenter.Data;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Protected Assets: browse/search/filter the assets currently registered with
/// DAAMS protection, read from Firestore's protected_assets collection.
/// Read-only presentation — actual protect/unprotect actions require the
/// Windows context menu + Monitoring System, which this app does not implement.
/// </summary>
public class ProtectedAssetsViewModel : DataSectionViewModelBase
{
    private readonly FirestoreService _firestore;
    private readonly ObservableCollection<ProtectedAsset> _allAssets = new();

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

    public ProtectedAssetsViewModel(FirestoreService firestore)
    {
        _firestore = firestore;

        Assets = CollectionViewSource.GetDefaultView(_allAssets);
        Assets.Filter = FilterPredicate;

        _selectedClassification = ClassificationOptions[0];
        _selectedStatus = StatusOptions[0];
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<ProtectedAsset> assets = await _firestore.GetProtectedAssetsAsync();
        Apply(assets);
        return assets.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetProtectedAssets());

    private void Apply(List<ProtectedAsset> assets)
    {
        _allAssets.Clear();
        foreach (ProtectedAsset asset in assets)
        {
            _allAssets.Add(asset);
        }

        TotalCount = _allAssets.Count;
        ProtectedCount = _allAssets.Count(a => a.Status == ProtectionStatus.Protected);
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
            bool matches = asset.AssetName.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || asset.Path.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
