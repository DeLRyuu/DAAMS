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
/// Incident Reports: manager-authored investigation records, read from
/// Firestore's incident_reports collection, optionally tied back to a
/// Security Alert. Read-only presentation — creating/editing incidents from
/// the Control Center is a later phase.
/// </summary>
public class IncidentReportsViewModel : DataSectionViewModelBase
{
    private readonly FirestoreService _firestore;
    private readonly ObservableCollection<IncidentReport> _allIncidents = new();

    public ICollectionView Incidents { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private int _openCount;
    public int OpenCount
    {
        get => _openCount;
        private set => SetProperty(ref _openCount, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Incidents.Refresh();
            }
        }
    }

    public List<FilterOption<InvestigationStatus>> StatusOptions { get; } = FilterOption<InvestigationStatus>.AllOptions("All Statuses");

    private FilterOption<InvestigationStatus> _selectedStatus;
    public FilterOption<InvestigationStatus> SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                Incidents.Refresh();
            }
        }
    }

    public IncidentReportsViewModel(FirestoreService firestore)
    {
        _firestore = firestore;

        Incidents = CollectionViewSource.GetDefaultView(_allIncidents);
        Incidents.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<IncidentReport> incidents = await _firestore.GetIncidentReportsAsync();
        Apply(incidents);
        return incidents.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetIncidentReports());

    private void Apply(List<IncidentReport> incidents)
    {
        _allIncidents.Clear();
        foreach (IncidentReport incident in incidents.OrderByDescending(i => i.UpdatedAt))
        {
            _allIncidents.Add(incident);
        }

        TotalCount = _allIncidents.Count;
        OpenCount = _allIncidents.Count(i => i.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.UnderInvestigation);
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not IncidentReport incident)
        {
            return false;
        }

        if (SelectedStatus.Value is InvestigationStatus status && incident.InvestigationStatus != status)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = incident.IncidentId.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || incident.User.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || incident.Asset.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || incident.Description.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
