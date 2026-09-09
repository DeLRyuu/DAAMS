using System.Collections.Generic;
using System.Windows.Input;

namespace ControlCenter.ViewModels;

/// <summary>
/// Root ViewModel for the application shell. Owns the currently selected
/// navigation section and swaps CurrentViewModel accordingly. Section
/// ViewModels are created once and reused (not recreated on every click),
/// so scroll position / filters entered later in Phases 3-7 will persist.
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
            [NavigationSection.ProtectedAssets] = new SectionPlaceholderViewModel(
                "Protected Assets",
                "Browse, search, and manage assets registered with DAAMS protection.",
                "Phase 3"),
            [NavigationSection.ActivityLogs] = new SectionPlaceholderViewModel(
                "Activity Logs",
                "Review detected activity on protected assets, with risk detail.",
                "Phase 4"),
            [NavigationSection.SecurityAlerts] = new SectionPlaceholderViewModel(
                "Security Alerts",
                "Review and investigate High/Critical activity that needs attention.",
                "Phase 5"),
            [NavigationSection.IncidentReports] = new SectionPlaceholderViewModel(
                "Incident Reports",
                "Document investigations and outcomes for flagged activity.",
                "Phase 6"),
            [NavigationSection.Settings] = new SectionPlaceholderViewModel(
                "Settings",
                "Notification preferences, risk display options, and system info.",
                "Phase 7"),
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
