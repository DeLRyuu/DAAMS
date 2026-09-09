using System;
using System.Collections.Generic;
using ControlCenter.Models;

namespace ControlCenter.Data;

/// <summary>
/// SAMPLE DATA ONLY.
///
/// This provider exists purely to demonstrate the Phase 1 layout and information
/// hierarchy before Firestore integration (Phase 8) exists. Nothing here is live
/// monitoring data, and no view should present it as such — every view that
/// consumes this must show a visible "Sample Data" indicator.
///
/// Field shapes intentionally mirror the DAAMS Monitoring System reference
/// (Section 10/11/13) so swapping this for a real Firestore-backed service
/// later requires no model changes.
/// </summary>
public static class MockDataProvider
{
    public static List<ActivityLog> GetRecentActivity() => new()
    {
        new ActivityLog
        {
            User = "Juan.DC",
            Device = "PC-FINANCE-01",
            Asset = "Payroll.xlsx",
            Path = @"C:\Company\Payroll.xlsx",
            Action = ActionType.Copy,
            Timestamp = DateTime.Now.AddMinutes(-12),
            Classification = Classification.Confidential,
            RiskScore = 83,
            RiskLevel = RiskLevel.Critical,
            RiskFactors = new List<RiskFactor>
            {
                new() { Description = "Classification: Confidential", Weight = 35 },
                new() { Description = "Action: Copy", Weight = 18 },
                new() { Description = "Off-hours access", Weight = 25 },
                new() { Description = "Burst / bulk activity", Weight = 5 }
            }
        },
        new ActivityLog
        {
            User = "Maria.S",
            Device = "PC-HR-02",
            Asset = "HR_Contracts",
            Path = @"C:\Company\HR_Contracts",
            Action = ActionType.Modify,
            Timestamp = DateTime.Now.AddMinutes(-40),
            Classification = Classification.Private,
            RiskScore = 12,
            RiskLevel = RiskLevel.Low,
            RiskFactors = new List<RiskFactor>
            {
                new() { Description = "Classification: Private", Weight = 25 },
                new() { Description = "Action: Modify", Weight = 8 }
            }
        },
        new ActivityLog
        {
            User = "Carlo.R",
            Device = "PC-PM-03",
            Asset = "ProjectPlans",
            Path = @"C:\Company\ProjectPlans",
            Action = ActionType.Rename,
            Timestamp = DateTime.Now.AddHours(-2),
            Classification = Classification.Sensitive,
            RiskScore = 33,
            RiskLevel = RiskLevel.Medium,
            RiskFactors = new List<RiskFactor>
            {
                new() { Description = "Classification: Sensitive", Weight = 15 },
                new() { Description = "Action: Rename", Weight = 6 },
                new() { Description = "Near edge of working hours", Weight = 8 }
            }
        },
        new ActivityLog
        {
            User = "Juan.DC",
            Device = "PC-FINANCE-01",
            Asset = "ClientDB.sqlite",
            Path = @"C:\Company\ClientDB.sqlite",
            Action = ActionType.Delete,
            Timestamp = DateTime.Now.AddHours(-5),
            Classification = Classification.Confidential,
            RiskScore = 61,
            RiskLevel = RiskLevel.High,
            RiskFactors = new List<RiskFactor>
            {
                new() { Description = "Classification: Confidential", Weight = 35 },
                new() { Description = "Action: Delete", Weight = 22 }
            }
        },
        new ActivityLog
        {
            User = "Maria.S",
            Device = "PC-HR-02",
            Asset = "CompanyHandbook.pdf",
            Path = @"C:\Company\CompanyHandbook.pdf",
            Action = ActionType.Open,
            Timestamp = DateTime.Now.AddHours(-6),
            Classification = Classification.Public,
            RiskScore = 2,
            RiskLevel = RiskLevel.Low,
            RiskFactors = new List<RiskFactor>
            {
                new() { Description = "Classification: Public", Weight = 5 },
                new() { Description = "Action: Open", Weight = 2 }
            }
        }
    };

    public static List<SecurityAlert> GetRecentAlerts() => new()
    {
        new SecurityAlert
        {
            AlertId = "ALT-1042",
            User = "Juan.DC",
            Device = "PC-FINANCE-01",
            Asset = "Payroll.xlsx",
            Action = ActionType.Copy,
            RiskScore = 83,
            RiskLevel = RiskLevel.Critical,
            Reason = "Confidential asset copied during off-hours.",
            Status = AlertStatus.New,
            CreatedAt = DateTime.Now.AddMinutes(-12)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1039",
            User = "Juan.DC",
            Device = "PC-FINANCE-01",
            Asset = "ClientDB.sqlite",
            Action = ActionType.Delete,
            RiskScore = 61,
            RiskLevel = RiskLevel.High,
            Reason = "Confidential asset deleted.",
            Status = AlertStatus.Investigating,
            CreatedAt = DateTime.Now.AddHours(-5)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1031",
            User = "Carlo.R",
            Device = "PC-PM-03",
            Asset = "ProjectPlans",
            Action = ActionType.Rename,
            RiskScore = 33,
            RiskLevel = RiskLevel.Medium,
            Reason = "Sensitive asset renamed near edge of working hours.",
            Status = AlertStatus.Reviewed,
            CreatedAt = DateTime.Now.AddHours(-2)
        }
    };

    public static List<ProtectedAsset> GetProtectedAssets() => new()
    {
        new ProtectedAsset { Path = @"C:\Company\Payroll.xlsx", AssetName = "Payroll.xlsx", AssetType = AssetType.File, Classification = Classification.Confidential, ProtectedAt = DateTime.Now.AddDays(-30), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\HR_Contracts", AssetName = "HR_Contracts", AssetType = AssetType.Folder, Classification = Classification.Private, ProtectedAt = DateTime.Now.AddDays(-30), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\ClientDB.sqlite", AssetName = "ClientDB.sqlite", AssetType = AssetType.File, Classification = Classification.Confidential, ProtectedAt = DateTime.Now.AddDays(-25), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\ProjectPlans", AssetName = "ProjectPlans", AssetType = AssetType.Folder, Classification = Classification.Sensitive, ProtectedAt = DateTime.Now.AddDays(-18), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\CompanyHandbook.pdf", AssetName = "CompanyHandbook.pdf", AssetType = AssetType.File, Classification = Classification.Public, ProtectedAt = DateTime.Now.AddDays(-60), ProtectedBy = "admin", Status = ProtectionStatus.Protected }
    };

    /// <summary>Counts of recent activity by risk band — feeds the Dashboard distribution bar.</summary>
    public static (int Low, int Medium, int High, int Critical) GetRiskDistribution() => (128, 41, 15, 4);

    public static int GetTotalProtectedAssets() => GetProtectedAssets().Count;

    public static int GetTotalActivitiesThisWeek() => 1284;
}
