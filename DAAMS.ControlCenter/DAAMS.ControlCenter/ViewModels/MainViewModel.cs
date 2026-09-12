using System.Collections.Generic;
using System.Windows.Input;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Root ViewModel for the application shell. Owns the currently selected
/// navigation section, the single shared Firestore connection, and swaps
/// CurrentViewModel on navigation. Section ViewModels are created once and
/// reused, and load their Firestore data lazily — the first time a section
/// becomes active — rather than all six querying Firestore at startup.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly Dictionary<NavigationSection, ViewModelBase> _sections;
    private readonly FirestoreConnectionService _firestoreConnection;

    private NavigationSection _currentSection;
    public NavigationSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                CurrentViewModel = _sections[_currentSection];
                (CurrentViewModel as IDataSection)?.EnsureLoaded();
            }
        }
    }

    private ViewModelBase _currentViewModel;
    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    /// <summary>Live Firestore connection status text for the shell footer.</summary>
    public string FirestoreStatusText => _firestoreConnection.StatusMessage;

    /// <summary>Live Firestore connection state for the shell footer's status dot.</summary>
    public FirestoreConnectionState FirestoreState => _firestoreConnection.State;

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        _firestoreConnection = new FirestoreConnectionService();
        _firestoreConnection.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FirestoreStatusText));
            OnPropertyChanged(nameof(FirestoreState));
        };

        var firestoreService = new FirestoreService(_firestoreConnection);

        _sections = new Dictionary<NavigationSection, ViewModelBase>
        {
            [NavigationSection.Dashboard] = new DashboardViewModel(firestoreService),
            [NavigationSection.ProtectedAssets] = new ProtectedAssetsViewModel(firestoreService),
            [NavigationSection.ActivityLogs] = new ActivityLogsViewModel(firestoreService),
            [NavigationSection.SecurityAlerts] = new SecurityAlertsViewModel(firestoreService),
            [NavigationSection.IncidentReports] = new IncidentReportsViewModel(firestoreService),
            [NavigationSection.Settings] = new SettingsViewModel(),
        };

        _currentSection = NavigationSection.Dashboard;
        _currentViewModel = _sections[NavigationSection.Dashboard];
        (_currentViewModel as IDataSection)?.EnsureLoaded();

        NavigateCommand = new RelayCommand(param =>
        {
            if (param is NavigationSection section)
            {
                CurrentSection = section;
            }
        });
    }
}
