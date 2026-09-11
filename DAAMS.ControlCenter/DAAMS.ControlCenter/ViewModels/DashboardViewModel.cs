using System.Collections.ObjectModel;
using System.Linq;
using ControlCenter.Data;
using ControlCenter.Models;

namespace ControlCenter.ViewModels;

/// <summary>
/// Dashboard: one primary insight, a row of secondary metrics, risk distribution,
/// then recent activity / recent alerts. All data here comes from the shared
/// MockDataProvider — see Data/MockDataProvider.cs — until Phase 8 Firestore
/// integration replaces it with a live-reading service behind the same shape.
/// </summary>
public class DashboardViewModel : ViewModelBase
{
    public bool IsSampleData { get; } = true;

    // --- Primary insight -------------------------------------------------
    public int HighRiskActivitiesThisWeek { get; }

    // --- Secondary metrics -------------------------------------------------
    public int TotalProtectedAssets { get; }
    public int TotalActivitiesThisWeek { get; }
    public int ActiveSecurityAlerts { get; }
    public int OpenIncidents { get; }

    // --- Risk distribution -------------------------------------------------
    public int RiskLowCount { get; }
    public int RiskMediumCount { get; }
    public int RiskHighCount { get; }
    public int RiskCriticalCount { get; }
    public int RiskTotalCount { get; }

    // --- Lists -------------------------------------------------
    public ObservableCollection<ActivityLog> RecentActivity { get; }
    public ObservableCollection<SecurityAlert> RecentAlerts { get; }

    public DashboardViewModel()
    {
        var recentActivity = MockDataProvider.GetRecentActivity(5);
        var recentAlerts = MockDataProvider.GetRecentAlerts(3);
        var allAlerts = MockDataProvider.GetSecurityAlerts();
        var distribution = MockDataProvider.GetRiskDistribution();

        RecentActivity = new ObservableCollection<ActivityLog>(recentActivity);
        RecentAlerts = new ObservableCollection<SecurityAlert>(recentAlerts);

        RiskLowCount = distribution.Low;
        RiskMediumCount = distribution.Medium;
        RiskHighCount = distribution.High;
        RiskCriticalCount = distribution.Critical;
        RiskTotalCount = RiskLowCount + RiskMediumCount + RiskHighCount + RiskCriticalCount;

        HighRiskActivitiesThisWeek = RiskHighCount + RiskCriticalCount;
        TotalProtectedAssets = MockDataProvider.GetTotalProtectedAssets();
        TotalActivitiesThisWeek = MockDataProvider.GetTotalActivitiesThisWeek();
        ActiveSecurityAlerts = allAlerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);
        OpenIncidents = MockDataProvider.GetOpenIncidentsCount();
    }
}
