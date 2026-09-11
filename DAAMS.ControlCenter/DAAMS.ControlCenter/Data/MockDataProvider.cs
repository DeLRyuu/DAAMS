using System;
using System.Collections.Generic;
using System.Linq;
using ControlCenter.Models;

namespace ControlCenter.Data;

/// <summary>
/// SAMPLE DATA ONLY.
///
/// This is the single, centralized source of mock data for the entire Control
/// Center. Every section (Dashboard, Protected Assets, Activity Logs, Security
/// Alerts, Incident Reports) reads from here instead of hardcoding its own
/// records, so the same users/devices/assets appear consistently everywhere —
/// exactly like a real Firestore-backed dataset would look once Phase 8 lands.
///
/// Nothing here is live monitoring data. No view may present it as such —
/// every consuming view must show a visible "Sample Data" indicator.
///
/// Field shapes intentionally mirror the DAAMS Monitoring System reference
/// (sections 10/11/13) so swapping this for a real Firestore-backed service
/// later requires no model changes, only a different data source behind the
/// same method signatures.
/// </summary>
public static class MockDataProvider
{
    // ------------------------------------------------------------------
    // Protected Assets
    // ------------------------------------------------------------------
    public static List<ProtectedAsset> GetProtectedAssets() => new()
    {
        new ProtectedAsset { Path = @"C:\Company\Payroll.xlsx", AssetName = "Payroll.xlsx", AssetType = AssetType.File, Classification = Classification.Confidential, ProtectedAt = DateTime.Now.AddDays(-30), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\HR_Contracts", AssetName = "HR_Contracts", AssetType = AssetType.Folder, Classification = Classification.Private, ProtectedAt = DateTime.Now.AddDays(-30), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\ClientDB.sqlite", AssetName = "ClientDB.sqlite", AssetType = AssetType.File, Classification = Classification.Confidential, ProtectedAt = DateTime.Now.AddDays(-25), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\ProjectPlans", AssetName = "ProjectPlans", AssetType = AssetType.Folder, Classification = Classification.Sensitive, ProtectedAt = DateTime.Now.AddDays(-18), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\CompanyHandbook.pdf", AssetName = "CompanyHandbook.pdf", AssetType = AssetType.File, Classification = Classification.Public, ProtectedAt = DateTime.Now.AddDays(-60), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\Finance\Q3_Forecast.xlsx", AssetName = "Q3_Forecast.xlsx", AssetType = AssetType.File, Classification = Classification.Private, ProtectedAt = DateTime.Now.AddDays(-14), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\Legal\NDAs", AssetName = "NDAs", AssetType = AssetType.Folder, Classification = Classification.Confidential, ProtectedAt = DateTime.Now.AddDays(-52), ProtectedBy = "admin", Status = ProtectionStatus.Protected },
        new ProtectedAsset { Path = @"C:\Company\Marketing\Old_Campaign_2024", AssetName = "Old_Campaign_2024", AssetType = AssetType.Folder, Classification = Classification.Sensitive, ProtectedAt = DateTime.Now.AddDays(-200), ProtectedBy = "admin", Status = ProtectionStatus.Unprotected },
    };

    // ------------------------------------------------------------------
    // Activity Log — the full sample set. Dashboard shows the most recent
    // slice of this same list rather than keeping a second hardcoded set.
    // ------------------------------------------------------------------
    public static List<ActivityLog> GetActivityLog()
    {
        var log = new List<ActivityLog>
        {
            new()
            {
                User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "Payroll.xlsx", Path = @"C:\Company\Payroll.xlsx",
                Action = ActionType.Copy, Timestamp = DateTime.Now.AddMinutes(-12),
                Classification = Classification.Confidential, RiskScore = 83, RiskLevel = RiskLevel.Critical,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Copy", Weight = 18 },
                    new() { Description = "Off-hours access", Weight = 25 },
                    new() { Description = "Burst / bulk activity", Weight = 5 }
                }
            },
            new()
            {
                User = "Maria.S", Device = "PC-HR-02", Asset = "HR_Contracts", Path = @"C:\Company\HR_Contracts",
                Action = ActionType.Modify, Timestamp = DateTime.Now.AddMinutes(-40),
                Classification = Classification.Private, RiskScore = 12, RiskLevel = RiskLevel.Low,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Private", Weight = 25 },
                    new() { Description = "Action: Modify", Weight = 8 }
                }
            },
            new()
            {
                User = "Carlo.R", Device = "PC-PM-03", Asset = "ProjectPlans", Path = @"C:\Company\ProjectPlans",
                Action = ActionType.Rename, Timestamp = DateTime.Now.AddHours(-2),
                Classification = Classification.Sensitive, RiskScore = 33, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Sensitive", Weight = 15 },
                    new() { Description = "Action: Rename", Weight = 6 },
                    new() { Description = "Near edge of working hours", Weight = 8 }
                }
            },
            new()
            {
                User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "ClientDB.sqlite", Path = @"C:\Company\ClientDB.sqlite",
                Action = ActionType.Delete, Timestamp = DateTime.Now.AddHours(-5),
                Classification = Classification.Confidential, RiskScore = 61, RiskLevel = RiskLevel.High,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Delete", Weight = 22 }
                }
            },
            new()
            {
                User = "Maria.S", Device = "PC-HR-02", Asset = "CompanyHandbook.pdf", Path = @"C:\Company\CompanyHandbook.pdf",
                Action = ActionType.Open, Timestamp = DateTime.Now.AddHours(-6),
                Classification = Classification.Public, RiskScore = 2, RiskLevel = RiskLevel.Low,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Public", Weight = 5 },
                    new() { Description = "Action: Open", Weight = 2 }
                }
            },
            new()
            {
                User = "Priya.N", Device = "PC-LEGAL-01", Asset = "NDAs", Path = @"C:\Company\Legal\NDAs",
                Action = ActionType.Open, Timestamp = DateTime.Now.AddHours(-7),
                Classification = Classification.Confidential, RiskScore = 37, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Open", Weight = 2 }
                }
            },
            new()
            {
                User = "Carlo.R", Device = "PC-PM-03", Asset = "Q3_Forecast.xlsx", Path = @"C:\Company\Finance\Q3_Forecast.xlsx",
                Action = ActionType.Modify, Timestamp = DateTime.Now.AddHours(-9),
                Classification = Classification.Private, RiskScore = 25, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Private", Weight = 25 }
                }
            },
            new()
            {
                User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "Payroll.xlsx", Path = @"C:\Company\Payroll.xlsx",
                Action = ActionType.Open, Timestamp = DateTime.Now.AddHours(-11),
                Classification = Classification.Confidential, RiskScore = 37, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Open", Weight = 2 }
                }
            },
            new()
            {
                User = "Maria.S", Device = "PC-HR-02", Asset = "HR_Contracts", Path = @"C:\Company\HR_Contracts",
                Action = ActionType.Copy, Timestamp = DateTime.Now.AddHours(-13),
                Classification = Classification.Private, RiskScore = 43, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Private", Weight = 25 },
                    new() { Description = "Action: Copy", Weight = 18 }
                }
            },
            new()
            {
                User = "Devon.K", Device = "PC-MKT-04", Asset = "Old_Campaign_2024", Path = @"C:\Company\Marketing\Old_Campaign_2024",
                Action = ActionType.Delete, Timestamp = DateTime.Now.AddHours(-15),
                Classification = Classification.Sensitive, RiskScore = 28, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Sensitive", Weight = 15 },
                    new() { Description = "Action: Delete", Weight = 22 }
                }
            },
            new()
            {
                User = "Carlo.R", Device = "PC-PM-03", Asset = "ProjectPlans", Path = @"C:\Company\ProjectPlans",
                Action = ActionType.Move, Timestamp = DateTime.Now.AddHours(-20),
                Classification = Classification.Sensitive, RiskScore = 29, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Sensitive", Weight = 15 },
                    new() { Description = "Action: Move", Weight = 14 }
                }
            },
            new()
            {
                User = "Maria.S", Device = "PC-HR-02", Asset = "CompanyHandbook.pdf", Path = @"C:\Company\CompanyHandbook.pdf",
                Action = ActionType.Modify, Timestamp = DateTime.Now.AddDays(-1),
                Classification = Classification.Public, RiskScore = 8, RiskLevel = RiskLevel.Low,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Public", Weight = 5 },
                    new() { Description = "Action: Modify", Weight = 8 }
                }
            },
            new()
            {
                User = "Priya.N", Device = "PC-LEGAL-01", Asset = "NDAs", Path = @"C:\Company\Legal\NDAs",
                Action = ActionType.Rename, Timestamp = DateTime.Now.AddDays(-1).AddHours(-3),
                Classification = Classification.Confidential, RiskScore = 41, RiskLevel = RiskLevel.Medium,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Rename", Weight = 6 }
                }
            },
            new()
            {
                User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "ClientDB.sqlite", Path = @"C:\Company\ClientDB.sqlite",
                Action = ActionType.Copy, Timestamp = DateTime.Now.AddDays(-2),
                Classification = Classification.Confidential, RiskScore = 53, RiskLevel = RiskLevel.High,
                RiskFactors = new List<RiskFactor>
                {
                    new() { Description = "Classification: Confidential", Weight = 35 },
                    new() { Description = "Action: Copy", Weight = 18 }
                }
            },
        };

        return log.OrderByDescending(a => a.Timestamp).ToList();
    }

    /// <summary>Most recent activity — used by the Dashboard. Same records, no duplicate data.</summary>
    public static List<ActivityLog> GetRecentActivity(int count = 5) => GetActivityLog().Take(count).ToList();

    // ------------------------------------------------------------------
    // Security Alerts
    // ------------------------------------------------------------------
    public static List<SecurityAlert> GetSecurityAlerts() => new()
    {
        new SecurityAlert
        {
            AlertId = "ALT-1042", User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "Payroll.xlsx",
            Action = ActionType.Copy, RiskScore = 83, RiskLevel = RiskLevel.Critical,
            Reason = "Confidential asset copied during off-hours.",
            Status = AlertStatus.New, CreatedAt = DateTime.Now.AddMinutes(-12)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1039", User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "ClientDB.sqlite",
            Action = ActionType.Delete, RiskScore = 61, RiskLevel = RiskLevel.High,
            Reason = "Confidential asset deleted.",
            Status = AlertStatus.Investigating, CreatedAt = DateTime.Now.AddHours(-5)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1037", User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "ClientDB.sqlite",
            Action = ActionType.Copy, RiskScore = 53, RiskLevel = RiskLevel.High,
            Reason = "Confidential asset copied twice within a short window (burst activity).",
            Status = AlertStatus.Investigating, CreatedAt = DateTime.Now.AddDays(-2)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1031", User = "Carlo.R", Device = "PC-PM-03", Asset = "ProjectPlans",
            Action = ActionType.Rename, RiskScore = 33, RiskLevel = RiskLevel.Medium,
            Reason = "Sensitive asset renamed near edge of working hours.",
            Status = AlertStatus.Reviewed, CreatedAt = DateTime.Now.AddHours(-2)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1024", User = "Priya.N", Device = "PC-LEGAL-01", Asset = "NDAs",
            Action = ActionType.Rename, RiskScore = 41, RiskLevel = RiskLevel.Medium,
            Reason = "Confidential folder contents renamed.",
            Status = AlertStatus.Resolved, CreatedAt = DateTime.Now.AddDays(-1).AddHours(-3)
        },
        new SecurityAlert
        {
            AlertId = "ALT-1018", User = "Devon.K", Device = "PC-MKT-04", Asset = "Old_Campaign_2024",
            Action = ActionType.Delete, RiskScore = 28, RiskLevel = RiskLevel.Medium,
            Reason = "Sensitive folder deleted; protection was later removed by admin.",
            Status = AlertStatus.Resolved, CreatedAt = DateTime.Now.AddDays(-3)
        },
    };

    /// <summary>Most recent alerts — used by the Dashboard. Same records, no duplicate data.</summary>
    public static List<SecurityAlert> GetRecentAlerts(int count = 3)
        => GetSecurityAlerts().OrderByDescending(a => a.CreatedAt).Take(count).ToList();

    // ------------------------------------------------------------------
    // Incident Reports — investigation records, optionally tied to an alert above.
    // ------------------------------------------------------------------
    public static List<IncidentReport> GetIncidentReports() => new()
    {
        new IncidentReport
        {
            IncidentId = "INC-204", RelatedAlertId = "ALT-1039",
            User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "ClientDB.sqlite",
            Action = ActionType.Delete, Timestamp = DateTime.Now.AddHours(-5),
            RiskLevel = RiskLevel.High,
            Description = "Confidential client database deleted outside a scheduled maintenance window.",
            InvestigationStatus = InvestigationStatus.UnderInvestigation,
            Notes = "Awaiting confirmation from Juan.DC on the reason for deletion.",
            CreatedAt = DateTime.Now.AddHours(-5), UpdatedAt = DateTime.Now.AddHours(-1)
        },
        new IncidentReport
        {
            IncidentId = "INC-201", RelatedAlertId = "ALT-1042",
            User = "Juan.DC", Device = "PC-FINANCE-01", Asset = "Payroll.xlsx",
            Action = ActionType.Copy, Timestamp = DateTime.Now.AddMinutes(-12),
            RiskLevel = RiskLevel.Critical,
            Description = "Payroll spreadsheet copied during off-hours; reason not yet established.",
            InvestigationStatus = InvestigationStatus.New,
            Notes = null,
            CreatedAt = DateTime.Now.AddMinutes(-10), UpdatedAt = DateTime.Now.AddMinutes(-10)
        },
        new IncidentReport
        {
            IncidentId = "INC-196", RelatedAlertId = "ALT-1024",
            User = "Priya.N", Device = "PC-LEGAL-01", Asset = "NDAs",
            Action = ActionType.Rename, Timestamp = DateTime.Now.AddDays(-1).AddHours(-3),
            RiskLevel = RiskLevel.Medium,
            Description = "NDA files renamed as part of a routine legal document cleanup.",
            InvestigationStatus = InvestigationStatus.Resolved,
            Notes = "Confirmed with Priya.N — scheduled housekeeping, no concern.",
            CreatedAt = DateTime.Now.AddDays(-1).AddHours(-2), UpdatedAt = DateTime.Now.AddDays(-1)
        },
        new IncidentReport
        {
            IncidentId = "INC-188", RelatedAlertId = "ALT-1018",
            User = "Devon.K", Device = "PC-MKT-04", Asset = "Old_Campaign_2024",
            Action = ActionType.Delete, Timestamp = DateTime.Now.AddDays(-3),
            RiskLevel = RiskLevel.Medium,
            Description = "Outdated marketing folder deleted after campaign archival was completed.",
            InvestigationStatus = InvestigationStatus.Dismissed,
            Notes = "Approved cleanup — no further action required.",
            CreatedAt = DateTime.Now.AddDays(-3), UpdatedAt = DateTime.Now.AddDays(-2)
        },
    };

    // ------------------------------------------------------------------
    // Dashboard summary figures
    // ------------------------------------------------------------------

    /// <summary>Counts of recent activity by risk band — feeds the Dashboard distribution bar.</summary>
    public static (int Low, int Medium, int High, int Critical) GetRiskDistribution() => (128, 41, 15, 4);

    public static int GetTotalProtectedAssets() => GetProtectedAssets().Count(a => a.Status == ProtectionStatus.Protected);

    public static int GetTotalActivitiesThisWeek() => 1284;

    public static int GetOpenIncidentsCount() => GetIncidentReports()
        .Count(i => i.InvestigationStatus is InvestigationStatus.New or InvestigationStatus.UnderInvestigation);
}
