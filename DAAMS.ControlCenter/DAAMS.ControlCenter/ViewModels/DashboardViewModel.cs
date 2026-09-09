using System.Collections.ObjectModel;
using System.Linq;
using ControlCenter.Data;
using ControlCenter.Models;

namespace ControlCenter.ViewModels;

/// <summary>
/// Dashboard: one primary insight, a small row of secondary metrics, then
/// recent activity / recent alerts. All data here is MockDataProvider sample
/// data — see Data/MockDataProvider.cs — until Phase 8 Firestore integration.
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
        var activity = MockDataProvider.GetRecentActivity();
        var alerts = MockDataProvider.GetRecentAlerts();
        var distribution = MockDataProvider.GetRiskDistribution();

        RecentActivity = new ObservableCollection<ActivityLog>(activity);
        RecentAlerts = new ObservableCollection<SecurityAlert>(alerts);

        RiskLowCount = distribution.Low;
        RiskMediumCount = distribution.Medium;
        RiskHighCount = distribution.High;
        RiskCriticalCount = distribution.Critical;
        RiskTotalCount = RiskLowCount + RiskMediumCount + RiskHighCount + RiskCriticalCount;

        HighRiskActivitiesThisWeek = RiskHighCount + RiskCriticalCount;
        TotalProtectedAssets = MockDataProvider.GetTotalProtectedAssets();
        TotalActivitiesThisWeek = MockDataProvider.GetTotalActivitiesThisWeek();
        ActiveSecurityAlerts = alerts.Count(a => a.Status is AlertStatus.New or AlertStatus.Investigating);
    }
}
