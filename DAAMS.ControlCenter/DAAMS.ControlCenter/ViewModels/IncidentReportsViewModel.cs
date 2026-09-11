using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using ControlCenter.Data;
using ControlCenter.Models;

namespace ControlCenter.ViewModels;

/// <summary>
/// Incident Reports: manager-authored investigation records, optionally tied
/// back to a Security Alert. UI/data presentation layer only for Phase 2 —
/// creating/editing incidents is a later phase.
/// </summary>
public class IncidentReportsViewModel : ViewModelBase
{
    public bool IsSampleData { get; } = true;

    private readonly ObservableCollection<IncidentReport> _allIncidents;
    public ICollectionView Incidents { get; }

    public int TotalCount => _allIncidents.Count;
    public int OpenCount => _allIncidents.Count(i => i.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.UnderInvestigation);

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

    public IncidentReportsViewModel()
    {
        _allIncidents = new ObservableCollection<IncidentReport>(
            MockDataProvider.GetIncidentReports().OrderByDescending(i => i.UpdatedAt));
        Incidents = CollectionViewSource.GetDefaultView(_allIncidents);
        Incidents.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
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
