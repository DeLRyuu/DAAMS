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
/// Security Alerts: High/Critical activity surfaced for manager review, read
/// from Firestore's security_alerts collection. An alert is a prompt for
/// investigation — not a finding of wrongdoing (reference doc §14/§15).
///
/// Phase 5 adds the investigation workflow: selecting an alert reveals its
/// full details and risk factors; the manager can move its status through
/// New → Investigating → Resolved/Dismissed and create a linked Incident
/// Report. These edits are LOCAL/in-memory only for this phase — Firestore
/// remains read-only (see FirestoreService) until real write-back is built
/// in a later phase. Alert *generation* still belongs entirely to the
/// Monitoring/Risk Assessment Engine; nothing here creates new alerts.
/// </summary>
public class SecurityAlertsViewModel : DataSectionViewModelBase
{
    private readonly FirestoreService _firestore;
    private readonly Action<IncidentReport> _createIncidentReport;
    private readonly ObservableCollection<SecurityAlert> _allAlerts = new();

    public ICollectionView Alerts { get; }

    private int _totalCount;
    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    private int _needsAttentionCount;
    public int NeedsAttentionCount
    {
        get => _needsAttentionCount;
        private set => SetProperty(ref _needsAttentionCount, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                Alerts.Refresh();
            }
        }
    }

    public List<FilterOption<AlertStatus>> StatusOptions { get; } = FilterOption<AlertStatus>.AllOptions("All Statuses");

    private FilterOption<AlertStatus> _selectedStatus;
    public FilterOption<AlertStatus> SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                Alerts.Refresh();
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
                Alerts.Refresh();
            }
        }
    }

    // --- Investigation workflow (selected alert) -------------------------------------------------

    /// <summary>The statuses an alert can be moved to from the investigation panel.</summary>
    public List<AlertStatus> EditableStatuses { get; } = Enum.GetValues<AlertStatus>().ToList();

    private SecurityAlert? _selectedAlert;
    public SecurityAlert? SelectedAlert
    {
        get => _selectedAlert;
        set
        {
            if (SetProperty(ref _selectedAlert, value))
            {
                NewIncidentNotes = string.Empty;
            }
        }
    }

    /// <summary>Draft notes typed while investigating the selected alert, used to seed a new Incident Report's Notes field.</summary>
    private string _newIncidentNotes = string.Empty;
    public string NewIncidentNotes
    {
        get => _newIncidentNotes;
        set => SetProperty(ref _newIncidentNotes, value);
    }

    /// <summary>Moves the selected alert to the status passed as CommandParameter (an AlertStatus).</summary>
    public ICommand ChangeStatusCommand { get; }

    /// <summary>Creates a new Incident Report pre-filled from the selected alert and hands it to IncidentReportsViewModel via the constructor-injected callback.</summary>
    public ICommand CreateIncidentReportCommand { get; }

    public SecurityAlertsViewModel(FirestoreService firestore, Action<IncidentReport> createIncidentReport)
    {
        _firestore = firestore;
        _createIncidentReport = createIncidentReport;

        Alerts = CollectionViewSource.GetDefaultView(_allAlerts);
        Alerts.Filter = FilterPredicate;

        _selectedStatus = StatusOptions[0];
        _selectedRiskLevel = RiskLevelOptions[0];

        ChangeStatusCommand = new RelayCommand(param =>
        {
            if (SelectedAlert is not null && param is AlertStatus newStatus)
            {
                ChangeStatus(SelectedAlert, newStatus);
            }
        });

        CreateIncidentReportCommand = new RelayCommand(_ =>
        {
            if (SelectedAlert is not null)
            {
                CreateIncidentFrom(SelectedAlert);
            }
        });
    }

    protected override async Task<int> LoadFromFirestoreAsync()
    {
        List<SecurityAlert> alerts = await _firestore.GetSecurityAlertsAsync();
        Apply(alerts);
        return alerts.Count;
    }

    protected override void LoadSampleFallbackCore() => Apply(MockDataProvider.GetSecurityAlerts());

    private void Apply(List<SecurityAlert> alerts)
    {
        SelectedAlert = null;

        _allAlerts.Clear();
        foreach (SecurityAlert alert in alerts.OrderByDescending(a => a.CreatedAt))
        {
            _allAlerts.Add(alert);
        }

        TotalCount = _allAlerts.Count;
        RecomputeNeedsAttention();
    }

    private void RecomputeNeedsAttention()
        => NeedsAttentionCount = _allAlerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);

    /// <summary>
    /// Mutates the alert's Status, then removes+re-inserts it in the backing
    /// ObservableCollection so the DataGrid regenerates that row's bindings
    /// (SecurityAlert isn't INotifyPropertyChanged — this is the deliberately
    /// simple alternative to adding change notification to a plain model,
    /// appropriate for a mini-capstone's scope).
    /// </summary>
    private void ChangeStatus(SecurityAlert alert, AlertStatus newStatus)
    {
        int index = _allAlerts.IndexOf(alert);
        if (index < 0)
        {
            return;
        }

        alert.Status = newStatus;
        _allAlerts.RemoveAt(index);
        _allAlerts.Insert(index, alert);
        SelectedAlert = alert;

        RecomputeNeedsAttention();
    }

    private void CreateIncidentFrom(SecurityAlert alert)
    {
        var incident = new IncidentReport
        {
            IncidentId = $"INC-{DateTime.Now:yyMMddHHmmss}",
            RelatedAlertId = alert.AlertId,
            User = alert.User,
            Device = alert.Device,
            Asset = alert.Asset,
            Action = alert.Action,
            Classification = alert.Classification,
            Timestamp = alert.CreatedAt,
            RiskLevel = alert.RiskLevel,
            Description = alert.Reason,
            InvestigationStatus = InvestigationStatus.New,
            Notes = string.IsNullOrWhiteSpace(NewIncidentNotes) ? null : NewIncidentNotes.Trim(),
            Resolution = null,
            DateResolved = null,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };

        _createIncidentReport(incident);
        NewIncidentNotes = string.Empty;
    }

    private bool FilterPredicate(object obj)
    {
        if (obj is not SecurityAlert alert)
        {
            return false;
        }

        if (SelectedStatus.Value is AlertStatus status && alert.Status != status)
        {
            return false;
        }

        if (SelectedRiskLevel.Value is RiskLevel riskLevel && alert.RiskLevel != riskLevel)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string term = SearchText.Trim();
            bool matches = alert.User.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.Device.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.Asset.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || alert.AlertId.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
