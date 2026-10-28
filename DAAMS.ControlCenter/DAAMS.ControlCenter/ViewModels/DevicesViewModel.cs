using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

public class DevicesViewModel : DataSectionViewModelBase
{
    private readonly LocalDatabaseService _db;
    private readonly Action<NavigationSection, string> _navigateWith;
    private readonly ObservableCollection<DeviceSummary> _allDevices = new();

    public ICollectionView Devices { get; }

    private DeviceSummary? _selectedDevice;
    public DeviceSummary? SelectedDevice
    {
        get => _selectedDevice;
        set => SetProperty(ref _selectedDevice, value);
    }

    public ICommand ViewActivityCommand { get; }
    public ICommand ViewCasesCommand { get; }

    public DevicesViewModel(LocalDatabaseService db, Action<NavigationSection, string> navigateWith, FirestoreService? firestore = null)
    {
        _db = db;
        _navigateWith = navigateWith;

        Devices = CollectionViewSource.GetDefaultView(_allDevices);

        ViewActivityCommand = new RelayCommand(_ =>
        {
            if (SelectedDevice != null)
            {
                _navigateWith(NavigationSection.ActivityLogs, SelectedDevice.Name);
            }
        });

        ViewCasesCommand = new RelayCommand(_ =>
        {
            if (SelectedDevice != null)
            {
                _navigateWith(NavigationSection.SecurityAlerts, SelectedDevice.Name);
            }
        });

        ViewContext.Current.RangeChanged += () =>
        {
            _ = RefreshAsync();
        };
    }

    public DevicesViewModel(Action<NavigationSection, string> navigateWith, FirestoreService? firestore = null)
        : this(new LocalDatabaseService(), navigateWith, firestore)
    {
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        var actTask = _db.GetActivityLogsAsync();
        var casesTask = _db.GetCasesAsync();
        await Task.WhenAll(actTask, casesTask);

        var summaries = DeviceService.Build(actTask.Result, casesTask.Result);
        Apply(summaries);
        return summaries.Count;
    }

    protected override void LoadSampleFallbackCore()
    {
    }

    private void Apply(List<DeviceSummary> summaries)
    {
        var sorted = summaries
            .OrderByDescending(d => d.OpenCases)
            .ThenByDescending(d => d.LastActivity)
            .ToList();

        string? savedName = SelectedDevice?.Name;

        _allDevices.Clear();
        foreach (var dev in sorted)
        {
            _allDevices.Add(dev);
        }

        if (!string.IsNullOrEmpty(savedName))
        {
            SelectedDevice = _allDevices.FirstOrDefault(d => string.Equals(d.Name, savedName, StringComparison.OrdinalIgnoreCase))
                             ?? _allDevices.FirstOrDefault();
        }
        else
        {
            SelectedDevice = _allDevices.FirstOrDefault();
        }
    }

    protected override void ApplyPendingSearch(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        var match = _allDevices.FirstOrDefault(d => string.Equals(d.Name, s.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            SelectedDevice = match;
        }
    }
}
