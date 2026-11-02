using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Base class for the Firestore-backed list sections (Protected Assets,
/// Activities, Security Alerts, Incident Reports). Handles the load/error/
/// empty state machine and the Refresh command so each concrete ViewModel only has to implement how to
/// fetch its own data.
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

    private string? _actionError;
    public string? ActionError
    {
        get => _actionError;
        protected set => SetProperty(ref _actionError, value);
    }

    public ICommand RefreshCommand { get; }

    protected DataSectionViewModelBase()
    {
        RefreshCommand = RelayCommand.FromAsync(_ => RefreshAsync(), ex => ActionError = ex.Message);
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
        if (LoadState != DataLoadState.Loaded)
        {
            LoadState = DataLoadState.Loading;
        }
        ErrorMessage = null;

        try
        {
            int count = await LoadFromFirestoreAsync();
            LoadState = count > 0 ? DataLoadState.Loaded : DataLoadState.Empty;
            ViewContext.Current.LastUpdated = DateTime.Now;

            var s = ViewContext.Current.TakePendingSearch();
            if (s != null)
            {
                ApplyPendingSearch(s);
            }
        }
        catch (Exception ex)
        {
            LoadState = DataLoadState.Error;
            ErrorMessage = "Unable to load data. Check local database and configuration.";
            Debug.WriteLine($"[{GetType().Name}] Data load failed: {ex}");
        }
    }

    protected virtual void ApplyPendingSearch(string s)
    {
    }

    /// <summary>Fetch from Firestore and populate this section's collection. Returns the item count. Let exceptions propagate — RefreshAsync handles them.</summary>
    protected abstract Task<int> LoadFromFirestoreAsync();
}
