using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ControlCenter.ViewModels;

/// <summary>
/// Base class for the four Firestore-backed list sections (Protected Assets,
/// Activity Logs, Security Alerts, Incident Reports). Handles the load/error/
/// empty state machine, the Refresh command, and the "view sample data
/// instead" fallback so each concrete ViewModel only has to implement how to
/// fetch its own data and how to load its own mock fallback.
/// </summary>
public abstract class DataSectionViewModelBase : ViewModelBase, IDataSection
{
    private bool _hasLoadedOnce;

    private DataLoadState _loadState = DataLoadState.Loading;
    public DataLoadState LoadState
    {
        get => _loadState;
        protected set => SetProperty(ref _loadState, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>True when the visible data is the Phase 2 mock fallback, not Firestore data.</summary>
    private bool _isShowingSampleFallback;
    public bool IsShowingSampleFallback
    {
        get => _isShowingSampleFallback;
        protected set => SetProperty(ref _isShowingSampleFallback, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ShowSampleDataCommand { get; }

    protected DataSectionViewModelBase()
    {
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        ShowSampleDataCommand = new RelayCommand(_ => LoadSampleFallback());
    }

    public void EnsureLoaded()
    {
        if (_hasLoadedOnce)
        {
            return;
        }

        _hasLoadedOnce = true;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        LoadState = DataLoadState.Loading;
        ErrorMessage = null;
        IsShowingSampleFallback = false;

        try
        {
            int count = await LoadFromFirestoreAsync();
            LoadState = count > 0 ? DataLoadState.Loaded : DataLoadState.Empty;
        }
        catch (Exception ex)
        {
            LoadState = DataLoadState.Error;
            ErrorMessage = "Unable to load data from Firestore. Check your connection and Firestore configuration.";
            Debug.WriteLine($"[{GetType().Name}] Firestore load failed: {ex}");
        }
    }

    private void LoadSampleFallback()
    {
        LoadSampleFallbackCore();
        IsShowingSampleFallback = true;
        ErrorMessage = null;
        LoadState = DataLoadState.Loaded;
    }

    /// <summary>Fetch from Firestore and populate this section's collection. Returns the item count. Let exceptions propagate — RefreshAsync handles them.</summary>
    protected abstract Task<int> LoadFromFirestoreAsync();

    /// <summary>Populate this section's collection from MockDataProvider instead — used only via the explicit "view sample data" action after a Firestore error.</summary>
    protected abstract void LoadSampleFallbackCore();
}
