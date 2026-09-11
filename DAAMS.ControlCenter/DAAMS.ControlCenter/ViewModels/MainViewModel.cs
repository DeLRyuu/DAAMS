using System.Collections.Generic;
using System.Windows.Input;

namespace ControlCenter.ViewModels;

/// <summary>
/// Root ViewModel for the application shell. Owns the currently selected
/// navigation section and swaps CurrentViewModel accordingly. Section
/// ViewModels are created once and reused (not recreated on every click),
/// so filters/search text entered in a section persist when navigating away
/// and back.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly Dictionary<NavigationSection, ViewModelBase> _sections;

    private NavigationSection _currentSection;
    public NavigationSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                CurrentViewModel = _sections[_currentSection];
            }
        }
    }

    private ViewModelBase _currentViewModel;
    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        _sections = new Dictionary<NavigationSection, ViewModelBase>
        {
            [NavigationSection.Dashboard] = new DashboardViewModel(),
            [NavigationSection.ProtectedAssets] = new ProtectedAssetsViewModel(),
            [NavigationSection.ActivityLogs] = new ActivityLogsViewModel(),
            [NavigationSection.SecurityAlerts] = new SecurityAlertsViewModel(),
            [NavigationSection.IncidentReports] = new IncidentReportsViewModel(),
            [NavigationSection.Settings] = new SettingsViewModel(),
        };

        _currentSection = NavigationSection.Dashboard;
        _currentViewModel = _sections[NavigationSection.Dashboard];

        NavigateCommand = new RelayCommand(param =>
        {
            if (param is NavigationSection section)
            {
                CurrentSection = section;
            }
        });
    }
}
