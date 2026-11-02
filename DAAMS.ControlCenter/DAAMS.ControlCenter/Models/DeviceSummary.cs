using System;
using System.Collections.Generic;

namespace ControlCenter.Models;

public record DeviceAssetCount(string Asset, int Count)
{
    public string CountText => $"{Count} {(Count == 1 ? "activity" : "activities")}";
}

public record DeviceHourBar(int Hour, double HeightPx, bool IsWorkHour, int Count = 0)
{
    public string TooltipText => $"{Hour:D2}:00 to {Hour:D2}:59, {Count} {(Count == 1 ? "activity" : "activities")}";
    public string Label => Hour % 6 == 0 ? $"{Hour:D2}:00" : string.Empty;
}

public class DeviceSummary
{
    public required string Name { get; init; }
    public List<string> Accounts { get; init; } = new();
    public string AccountsText => Accounts.Count > 0 ? string.Join(", ", Accounts) : "None";
    public DateTime FirstSeen { get; init; }
    public DateTime LastActivity { get; init; }

    public string LastActivityText
    {
        get
        {
            var date = LastActivity.Date;
            var today = DateTime.Today;
            if (date == today) return $"today at {LastActivity:HH:mm}";
            if (date == today.AddDays(-1)) return $"yesterday at {LastActivity:HH:mm}";
            int days = (today - date).Days;
            if (days > 1) return $"{days} days ago";
            return $"{LastActivity:yyyy-MM-dd HH:mm}";
        }
    }

    public int Events24h { get; init; }
    public int Events7d { get; init; }
    public int OpenCases { get; init; }
    public RiskLevel? HighestRisk { get; init; }
    public List<DeviceAssetCount> TopAssets { get; init; } = new();
    public int[] HourCounts { get; init; } = new int[24];
    public List<DeviceHourBar> HourBars { get; init; } = new();
}
