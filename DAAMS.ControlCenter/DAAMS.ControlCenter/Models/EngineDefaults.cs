// Source: monitoring_engine/config/settings.py
using System.Linq;

namespace ControlCenter.Models;

public static class EngineDefaults
{
    public const int WorkStartHour = 7;
    public const int WorkEndHour = 19;
    public const int OffHoursPenalty = 25;
    public const int NearEdgeMinutes = 30;
    public const int NearEdgePenalty = 8;
    public const int BurstThreshold = 3;
    public const int BurstWindowSeconds = 60;
    public const int BurstPenalty = 20;

    public static readonly (string Name, int Weight)[] Classifications =
    {
        ("Public", 5),
        ("Sensitive", 15),
        ("Private", 25),
        ("Confidential", 35)
    };

    public static readonly (string Name, int Weight)[] Actions =
    {
        ("Open", 2),
        ("Modify", 8),
        ("Rename", 6),
        ("Copy", 18),
        ("Move", 14),
        ("Delete", 22)
    };

    public static string ClassificationText =>
        string.Join(", ", Classifications.Select(c => $"{c.Name} {c.Weight}"));

    public static string ActionText =>
        string.Join(", ", Actions.Select(a => $"{a.Name} {a.Weight}"));

    public static string WorkingHoursText => $"{WorkStartHour:00}:00 to {WorkEndHour:00}:00";
    public static string OffHoursText => $"+{OffHoursPenalty}";
    public static string NearEdgeText => $"near the boundary ({NearEdgeMinutes} minutes) +{NearEdgePenalty}";
    public static string BurstText => $"{BurstThreshold} actions in {BurstWindowSeconds} seconds +{BurstPenalty}";
    public static string RiskBandsText => "Low 0-24, Medium 25-49, High 50-74, Critical 75 and above";
}
