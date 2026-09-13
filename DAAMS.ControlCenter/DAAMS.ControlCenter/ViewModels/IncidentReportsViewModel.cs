using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using ControlCenter.Data;
using ControlCenter.Models;
using ControlCenter.Services;

namespace ControlCenter.ViewModels;

/// <summary>
/// Incident Reports: manager-authored investigation records, read from
/// Firestore's incident_reports collection, optionally tied back to a
/// Security Alert.
///
/// Phase 5 adds: a Risk Level filter (Status filter already existed),
/// selecting an incident to review/edit its full details, a local status
/// workflow (New → Under Investigation → Resolved/Dismissed) with an
/// editable resolution note, and AddLocalIncident() — the landing spot for
/// incidents created from the Security Alerts investigation panel. As with
/// Security Alerts, all edits here are local/in-memory for this phase;
/// Firestore stays read-only until real write-back is built later.
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

    public List<FilterOption<RiskLevel>> RiskLevelOptions { get; } = FilterOption<RiskLevel>.AllOptions("All Risk Levels");

    private FilterOption<RiskLevel> _selectedRiskLevel;
    public FilterOption<RiskLevel> SelectedRiskLevel
    {
        get => _selectedRiskLevel;
        set
        {
            if (SetProperty(ref _selectedRiskLevel, value))
            {
                Incidents.Refresh();
            }
        }
    }

    // --- Investigation workflow (selected incident) -------------------------------------------------

    public List<InvestigationStatus> EditableStatuses { get; } = Enum.GetValues<InvestigationStatus>().ToList();

    private IncidentReport? _selectedIncident;
    public IncidentReport? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (SetProperty(ref _selectedIncident, value))
            {
                ResolutionDraft = value?.Resolution ?? string.Empty;
            }
        }
    }

    /// <summary>Editable draft bound to the resolution TextBox — applied via SaveResolutionCommand rather than live, so a half-typed note is never silently committed.</summary>
    private string _resolutionDraft = string.Empty;
    public string ResolutionDraft
    {
        get => _resolutionDraft;
        set => SetProperty(ref _resolutionDraft, value);
    }

    /// <summary>Moves the selected incident to the status passed as CommandParameter (an InvestigationStatus). Marking Resolved or Dismissed also stamps DateResolved.</summary>
    public ICommand ChangeStatusCommand { get; }

    /// <summary>Commits ResolutionDraft into the selected incident's Resolution field.</summary>
    public ICommand SaveResolutionCommand { get; }

    public IncidentReportsViewModel(FirestoreService firestore)
    {
        _firestore = firestore;

        Incidents = CollectionViewSource.GetDefaultView(_allIncidents);
        Incidents.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];

        ChangeStatusCommand = new RelayCommand(param =>
        {
            if (SelectedIncident is not null && param is InvestigationStatus newStatus)
            {
                ChangeStatus(SelectedIncident, newStatus);
            }
        });

        SaveResolutionCommand = new RelayCommand(_ =>
        {
            if (SelectedIncident is not null)
            {
                SaveResolution(SelectedIncident);
            }
        });
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<IncidentReport> incidents = await _firestore.GetIncidentReportsAsync();
        Apply(incidents);
        return incidents.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetIncidentReports());

    /// <summary>
    /// Adds an incident created from the Security Alerts investigation panel
    /// straight into the visible list, without a full reload. Session-only —
    /// like every other edit in this phase, it isn't written to Firestore.
    /// </summary>
    public void AddLocalIncident(IncidentReport incident)
    {
        _allIncidents.Insert(0, incident);
        TotalCount = _allIncidents.Count;
        RecomputeOpenCount();
        SelectedIncident = incident;
    }

    private void Apply(List<IncidentReport> incidents)
    {
        SelectedIncident = null;

        _allIncidents.Clear();
        foreach (IncidentReport incident in incidents.OrderByDescending(i => i.UpdatedAt))
        {
            _allIncidents.Add(incident);
        }

        TotalCount = _allIncidents.Count;
        RecomputeOpenCount();
    }

    private void RecomputeOpenCount()
        => OpenCount = _allIncidents.Count(i => i.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.UnderInvestigation);

    /// <summary>
    /// Mutates the incident's status (and, for Resolved/Dismissed, stamps
    /// DateResolved) then removes+re-inserts it in the backing
    /// ObservableCollection so the DataGrid regenerates that row's bindings —
    /// same reasoning as SecurityAlertsViewModel.ChangeStatus.
    /// </summary>
    private void ChangeStatus(IncidentReport incident, InvestigationStatus newStatus)
    {
        int index = _allIncidents.IndexOf(incident);
        if (index < 0)
        {
            return;
        }

        incident.InvestigationStatus = newStatus;
        incident.UpdatedAt = DateTime.Now;

        if (newStatus is InvestigationStatus.Resolved or InvestigationStatus.Dismissed)
        {
            incident.DateResolved = DateTime.Now;
        }
        else
        {
            incident.DateResolved = null;
        }

        _allIncidents.RemoveAt(index);
        _allIncidents.Insert(index, incident);
        SelectedIncident = incident;

        RecomputeOpenCount();
    }

    private void SaveResolution(IncidentReport incident)
    {
        int index = _allIncidents.IndexOf(incident);
        if (index < 0)
        {
            return;
        }

        incident.Resolution = string.IsNullOrWhiteSpace(ResolutionDraft) ? null : ResolutionDraft.Trim();
        incident.UpdatedAt = DateTime.Now;

        _allIncidents.RemoveAt(index);
        _allIncidents.Insert(index, incident);
        SelectedIncident = incident;
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

        if (SelectedRiskLevel.Value is RiskLevel riskLevel && incident.RiskLevel != riskLevel)
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
