using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Root ViewModel for the application shell. Owns the currently selected
/// navigation section, the single shared Firestore connection, and swaps
/// CurrentViewModel on navigation. Section ViewModels are created once and
/// reused, and load their data when a section becomes active rather than all
/// querying Firestore at startup.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly Dictionary<NavigationSection, ViewModelBase> _sections;
    private readonly FirestoreConnectionService _firestoreConnection;
    private readonly LocalDatabaseService _dbService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly HashSet<string> _seenAlertIds = new();
    private bool _initialAlertsSeeded;

    private NavigationSection _currentSection;
    public NavigationSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (SetProperty(ref _currentSection, value))
            {
                OnPropertyChanged(nameof(IsRangeBarVisible));
                if (_sections.TryGetValue(_currentSection, out var sectionVm))
                {
                    CurrentViewModel = sectionVm;
                    _ = (sectionVm as IDataSection)?.RefreshAsync();
                }
                else
                {
                    CurrentViewModel = null;
                }
                _ = RefreshSyncStatusAsync();
            }
        }
    }

    public bool IsRangeBarVisible => CurrentSection is not (NavigationSection.Settings or NavigationSection.Devices or NavigationSection.IncidentReports);

    public void NavigateWith(NavigationSection section, string search)
    {
        ViewContext.Current.PendingSearch = search;
        CurrentSection = section;
    }

    private ViewModelBase? _currentViewModel;
    public ViewModelBase? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    /// <summary>Live Firestore connection status text for the shell footer.</summary>
    public string FirestoreStatusText => "Firestore: not connected (using local database)";

    /// <summary>Live Firestore connection state for the shell footer's status dot.</summary>
    public FirestoreConnectionState FirestoreState => _firestoreConnection.State;

    private string _localDbStatusText = "Local DB: Active";
    public string LocalDbStatusText
    {
        get => _localDbStatusText;
        private set => SetProperty(ref _localDbStatusText, value);
    }

    public string LocalDbPath => _dbService.DatabasePath;
    public string LastUpdatedText => ViewContext.Current.LastUpdatedText;

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        ViewContext.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewContext.LastUpdatedText))
            {
                OnPropertyChanged(nameof(LastUpdatedText));
            }
        };
        _dbService = new LocalDatabaseService();
        _firestoreConnection = new FirestoreConnectionService();
        _firestoreConnection.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FirestoreStatusText));
            OnPropertyChanged(nameof(FirestoreState));
            _ = RefreshSyncStatusAsync();
        };

        var firestoreService = new FirestoreService(_firestoreConnection);

        var autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };

        CasesViewModel = new CasesViewModel(_dbService, NavigateWith, firestoreService);
        _ = CasesViewModel.RefreshAsync();

        _settingsViewModel = new SettingsViewModel(_dbService, autoRefreshTimer, () => _firestoreConnection.StatusMessage);
        ViewContext.Current.Range = _settingsViewModel.DefaultTimeRange;

        IncidentReportsViewModel = new IncidentReportsViewModel(_dbService, firestoreService);

        _sections = new Dictionary<NavigationSection, ViewModelBase>
        {
            [NavigationSection.Dashboard] = new DashboardViewModel(_dbService, NavigateWith),
            [NavigationSection.ProtectedAssets] = new ProtectedAssetsViewModel(_dbService, firestoreService),
            [NavigationSection.ActivityLogs] = new ActivityLogsViewModel(_dbService, NavigateWith, firestoreService),
            [NavigationSection.Devices] = new DevicesViewModel(_dbService, NavigateWith, firestoreService),
            [NavigationSection.SecurityAlerts] = CasesViewModel,
            [NavigationSection.IncidentReports] = IncidentReportsViewModel,
            [NavigationSection.Settings] = _settingsViewModel,
        };

        _currentSection = NavigationSection.Dashboard;
        _currentViewModel = _sections[NavigationSection.Dashboard];
        _ = (_currentViewModel as IDataSection)?.RefreshAsync();
        _ = RefreshSyncStatusAsync();

        NavigateCommand = new RelayCommand(param =>
        {
            if (param is NavigationSection section)
            {
                if (section == NavigationSection.ActivityLogs)
                {
                    ViewContext.Current.PendingSearch = null;
                    if (_sections.TryGetValue(NavigationSection.ActivityLogs, out var actVm) && actVm is ActivityLogsViewModel vm)
                    {
                        vm.SearchText = string.Empty;
                    }
                }
                CurrentSection = section;
            }
        });

        autoRefreshTimer.Tick += async (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (CurrentViewModel is IDataSection section)
            {
                await section.RefreshAsync();
            }

            await CheckForNewAlertNotificationsAsync();
        };
        _settingsViewModel.ApplyAutoRefreshTimer();

        _ = CheckForNewAlertNotificationsAsync();
    }

    public async Task RefreshSyncStatusAsync()
    {
        if (!_dbService.DatabaseFound)
        {
            LocalDbStatusText = "Local DB: NOT FOUND";
            return;
        }

        try
        {
            var (total, pending) = await _dbService.GetSyncSummaryAsync();
            if (pending > 0)
            {
                LocalDbStatusText = $"Local DB: Active ({pending} pending sync)";
            }
            else
            {
                LocalDbStatusText = $"Local DB: Active ({total} records, Synced)";
            }
        }
        catch
        {
            LocalDbStatusText = "Local DB: Error reading database";
        }
    }

    private async Task CheckForNewAlertNotificationsAsync()
    {
        if (!_dbService.DatabaseFound) return;

        try
        {
            var cases = await _dbService.GetCasesAsync();
            if (!_initialAlertsSeeded)
            {
                foreach (var c in cases)
                {
                    _seenAlertIds.Add(c.AlertId);
                }
                _initialAlertsSeeded = true;
                return;
            }

            foreach (var c in cases)
            {
                if (c.Status == CaseStatus.New &&
                    (c.RiskLevel == RiskLevel.High || c.RiskLevel == RiskLevel.Critical) &&
                    !_seenAlertIds.Contains(c.AlertId))
                {
                    _seenAlertIds.Add(c.AlertId);
                    if (_settingsViewModel.NotifyOnNewAlert)
                    {
                        ShowToastNotification($"{c.RiskLevel} Risk Alert", $"{c.Asset}: {c.Reason}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] Alert check error: {ex}");
        }
    }

    private void ShowToastNotification(string title, string message)
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var window = Application.Current.MainWindow;
            if (window == null) return;

            Panel? targetPanel = window.Content as Panel;
            if (targetPanel == null && window.Content is Decorator decorator)
            {
                targetPanel = decorator.Child as Panel;
            }

            if (targetPanel == null) return;

            var toast = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 24, 0),
                Padding = new Thickness(16, 12, 16, 12),
                Background = Application.Current.TryFindResource("Brush.SurfaceElevated") as Brush ?? new SolidColorBrush(Color.FromRgb(35, 38, 47)),
                BorderBrush = Application.Current.TryFindResource("Brush.RiskCritical") as Brush ?? new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                MaxWidth = 360,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = title,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Application.Current.TryFindResource("Brush.TextPrimary") as Brush ?? Brushes.White,
                            Margin = new Thickness(0, 0, 0, 4)
                        },
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Application.Current.TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.LightGray
                        }
                    }
                }
            };

            Panel.SetZIndex(toast, 99999);
            if (targetPanel is Grid)
            {
                Grid.SetRowSpan(toast, 100);
                Grid.SetColumnSpan(toast, 100);
            }

            targetPanel.Children.Add(toast);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                targetPanel.Children.Remove(toast);
            };
            timer.Start();
        });
    }

    public CasesViewModel CasesViewModel { get; }
    public IncidentReportsViewModel IncidentReportsViewModel { get; }
}
